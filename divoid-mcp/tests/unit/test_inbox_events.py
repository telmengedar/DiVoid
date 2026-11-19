"""Fixed inbox framing, credential custody, cancellation and one-request CLI mode."""

from __future__ import annotations

import asyncio
import json
import sys

import httpx
import pytest
import respx

from divoid_mcp import cli, http_client, inbox_events, inbox_sse, server
from divoid_mcp.config import DivoidConfig
from divoid_mcp.inbox_event_error import InboxEventError

READY = {"version": 1, "coverage": "postgres-database", "replay": False,
         "resyncRequired": True}
FRAME = b"event:ready\ndata:" + json.dumps(READY).encode() + b"\n\n"
DIRTY = b"event:invalidate\ndata:{}\n\n"


async def chunks(body, stride=1):
    for offset in range(0, len(body), stride):
        yield body[offset:offset + stride]


@pytest.mark.asyncio
async def test_bounded_decoder_retains_actual_ready_and_invalidate_before_eof():
    received = []
    with pytest.raises(InboxEventError, match="eof"):
        async for event in inbox_sse.decode(chunks(FRAME + b":keepalive\n\n" + DIRTY)):
            received.append(event)
    assert received == [{"event": "ready", **READY}, {"event": "invalidate"}]


@pytest.mark.parametrize("body", [
    DIRTY, FRAME + FRAME, b"event:ready\n\n", b"id:1\n" + FRAME,
    FRAME.replace(b'"replay": false', b'"replay": 0'),
    FRAME.replace(b'"version": 1', b'"version": true'),
    FRAME.replace(b'"version": 1', b'"version": 1,"version":1'),
    FRAME.replace(b'"version": 1', b'"privateBody":"secret","version":1'),
    b"x" * 4097, b":" + b"x" * 4097 + b"\n", b"\xff", b"event:ready\n",
    FRAME + b"event:invalidate\ndata:{\"messageId\":7}\n\n",
])
@pytest.mark.asyncio
async def test_malformed_or_unbounded_frames_fail_without_payload_output(body):
    with pytest.raises(InboxEventError):
        async for event in inbox_sse.decode(chunks(body, 1024)):
            assert event == {"event": "ready", **READY}


@pytest.mark.parametrize("status,body,permanent", [
    (401, {}, True), (403, {}, True), (404, {}, True), (302, {}, True),
    (503, {"code": "inbox_changes_provider_unsupported"}, True),
    (503, {"code": "inbox_changes_unavailable"}, False), (429, {}, False), (500, {}, False),
])
@respx.mock
@pytest.mark.asyncio
async def test_http_errors_never_follow_or_expose_payload(status, body, permanent):
    http_client.init("http://divoid.test", "private-key-canary")
    route = respx.get("http://divoid.test/v1/messagechanges").mock(
        return_value=httpx.Response(status, json=body,
                                    headers={"location": "http://other.invalid/private"})
    )
    try:
        with pytest.raises(InboxEventError) as caught:
            async for _ in http_client.inbox_events():
                pytest.fail("error fabricated ready")
        assert caught.value.permanent is permanent
        assert "private" not in str(caught.value)
        assert route.call_count == 1
        assert len(respx.calls) == 1
        assert route.calls[0].request.headers["authorization"] == "Bearer private-key-canary"
        assert not route.calls[0].request.url.query
    finally:
        await http_client.close()


@respx.mock
@pytest.mark.asyncio
async def test_cli_fixed_ndjson_no_inbox_get_no_reconnect_and_safe_eof(monkeypatch, capsys):
    monkeypatch.setattr(inbox_events, "load_secret", lambda: DivoidConfig(
        base_url="http://divoid.test", api_key="private-key-canary", source="env"))
    route = respx.get("http://divoid.test/v1/messagechanges").mock(return_value=httpx.Response(
        200, content=FRAME + DIRTY, headers={"content-type": "text/event-stream"}))
    assert await inbox_events._run() == 1
    captured = capsys.readouterr()
    assert [json.loads(line) for line in captured.out.splitlines()] == [
        {"event": "ready", **READY}, {"event": "invalidate"}]
    assert captured.err.strip() == "inbox_events error=eof"
    assert "private-key-canary" not in captured.out + captured.err
    assert route.call_count == 1
    assert len(respx.calls) == 1
    assert http_client._client is None


@pytest.mark.asyncio
async def test_bridge_flushes_before_eof_and_cancels_owned_resources(monkeypatch, capsys):
    monkeypatch.setattr(inbox_events, "load_secret", lambda: DivoidConfig(
        base_url="http://divoid.test", api_key="dummy", source="env"))
    produced = asyncio.Event()
    closed = asyncio.Event()

    async def events():
        try:
            yield {"event": "ready", **READY}
            produced.set()
            await asyncio.Future()
        finally:
            closed.set()

    monkeypatch.setattr(http_client, "inbox_events", events)
    running = asyncio.create_task(inbox_events._run())
    await produced.wait()
    assert not running.done()
    assert json.loads(capsys.readouterr().out) == {"event": "ready", **READY}
    running.cancel()
    assert await running == 130
    assert closed.is_set()
    assert http_client._client is None


def test_cli_default_mode_and_event_mode_are_distinct(monkeypatch):
    calls = []
    monkeypatch.setattr(server, "main", lambda: calls.append("mcp"))
    monkeypatch.setattr(inbox_events, "main", lambda: 2)
    monkeypatch.setattr(sys, "argv", ["divoid-mcp"])
    assert cli.main() is None
    assert calls == ["mcp"]
    monkeypatch.setattr(sys, "argv", ["divoid-mcp", "--inbox-events"])
    assert cli.main() == 2
    assert calls == ["mcp"]


def test_unknown_arguments_do_not_echo_sensitive_values(monkeypatch, capsys):
    monkeypatch.setattr(sys, "argv", ["divoid-mcp", "--inbox-events", "private-canary"])
    assert cli.main() == 2
    captured = capsys.readouterr()
    assert captured.out == ""
    assert captured.err == "divoid_mcp error=arguments\n"


@respx.mock
@pytest.mark.asyncio
async def test_cli_outputs_only_backend_records_and_sanitized_eof(monkeypatch, capsys):
    monkeypatch.setattr(inbox_events, "load_secret", lambda: DivoidConfig(
        base_url="http://divoid.test", api_key="private-key-canary", source="env"))
    route = respx.get("http://divoid.test/v1/messagechanges").mock(
        return_value=httpx.Response(200, content=FRAME + DIRTY,
                                    headers={"content-type": "text/event-stream"}))
    assert await inbox_events._run() == 1
    captured = capsys.readouterr()
    assert [json.loads(line) for line in captured.out.splitlines()] == [
        {"event": "ready", **READY}, {"event": "invalidate"}]
    assert captured.err == "inbox_events error=eof\n"
    assert route.call_count == 1
    assert http_client._client is None


@pytest.mark.asyncio
async def test_cli_configuration_failure_is_fixed_and_permanent(monkeypatch, capsys):
    def fail():
        import logging
        logging.getLogger("divoid_mcp.config").error("private-config-canary")
        raise SystemExit(1)
    monkeypatch.setattr(inbox_events, "load_secret", fail)
    assert await inbox_events._run() == 2
    captured = capsys.readouterr()
    assert captured.out == ""
    assert captured.err == "inbox_events error=configuration\n"


@pytest.mark.asyncio
async def test_cli_cleanup_failure_never_leaks_exception(monkeypatch, capsys):
    monkeypatch.setattr(inbox_events, "load_secret", lambda: DivoidConfig(
        base_url="http://divoid.test", api_key="private-key-canary", source="env"))
    async def source():
        raise InboxEventError("eof")
        yield {}
    async def close():
        raise RuntimeError("private-cleanup-canary")
    original_close = http_client.close
    monkeypatch.setattr(http_client, "inbox_events", source)
    monkeypatch.setattr(http_client, "close", close)
    assert await inbox_events._run() == 1
    captured = capsys.readouterr()
    assert captured.out == ""
    assert captured.err == "inbox_events error=eof\ninbox_events error=close\n"
    assert "private" not in captured.err
    await original_close()


class OpenInboxStream(httpx.AsyncByteStream):
    def __init__(self):
        self.release = asyncio.Event()
        self.closed = asyncio.Event()

    async def __aiter__(self):
        yield FRAME
        await self.release.wait()
        yield DIRTY
        await asyncio.Future()

    async def aclose(self):
        self.closed.set()


@pytest.mark.asyncio
async def test_real_httpx_iterator_delivers_short_frames_before_eof(monkeypatch):
    stream = OpenInboxStream()
    client = httpx.AsyncClient(transport=httpx.MockTransport(lambda request:
        httpx.Response(200, headers={"content-type": "text/event-stream"}, stream=stream)))
    monkeypatch.setattr(http_client, "_client", client)
    monkeypatch.setattr(http_client, "_base_url", "http://divoid.test")
    events = http_client.inbox_events()
    try:
        assert await asyncio.wait_for(anext(events), 1) == {"event": "ready", **READY}
        assert not stream.closed.is_set()
        stream.release.set()
        assert await asyncio.wait_for(anext(events), 1) == {"event": "invalidate"}
        assert not stream.closed.is_set()
    finally:
        await events.aclose()
        await client.aclose()
    assert stream.closed.is_set()


@respx.mock
@pytest.mark.asyncio
async def test_large_coalesced_transport_chunk_retains_bounded_valid_frames():
    http_client.init("http://divoid.test", "dummy")
    body = FRAME + DIRTY * 256
    assert len(body) > 4096
    respx.get("http://divoid.test/v1/messagechanges").mock(return_value=httpx.Response(
        200, content=body, headers={"content-type": "text/event-stream"}))
    records = []
    try:
        with pytest.raises(InboxEventError, match="eof"):
            async for record in http_client.inbox_events():
                records.append(record)
    finally:
        await http_client.close()
    assert records == [{"event": "ready", **READY}] + [{"event": "invalidate"}] * 256
