"""Recipient reply tools preserve explicit-send and uncertain receipt semantics."""

from __future__ import annotations

import json

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools import register_tools


@pytest.fixture
def server():
    http_client.init("http://divoid.test", "dummy")
    server = FastMCP("reply")
    server.config = DivoidConfig(base_url="http://divoid.test", api_key="dummy", source="env")
    register_tools(server)
    return server


@pytest.mark.parametrize("tool,method,payload", [
    ("divoid_reply_message", "POST", {"message_id": 7, "body": "explicit response"}),
    ("divoid_get_message_reply", "GET", {"message_id": 7}),
])
@respx.mock
async def test_registered_tools_use_only_original_message_route(server, tool, method, payload):
    receipt = {"messageId": 7, "transport": "teams", "state": "sent", "outboundActivityId": "reply-1"}
    route = respx.request(method, "http://divoid.test/messages/7/reply").mock(
        return_value=httpx.Response(200, json=receipt)
    )

    result = await server._tool_manager.call_tool(tool, payload)

    assert route.call_count == 1
    assert result == receipt
    if method == "POST":
        assert json.loads(route.calls[0].request.content) == {"body": "explicit response"}
    else:
        assert not route.calls[0].request.content
    assert not route.calls[0].request.url.query


@pytest.mark.parametrize("tool", ["divoid_reply_message", "divoid_get_message_reply"])
@pytest.mark.parametrize("identifier", [0, -3])
@respx.mock
async def test_invalid_message_id_makes_no_request(server, tool, identifier):
    payload = {"message_id": identifier}
    if tool == "divoid_reply_message":
        payload["body"] = "reply"

    result = await server._tool_manager.call_tool(tool, payload)

    assert result["isError"]
    assert not respx.calls


@pytest.mark.parametrize("body", ["", "  ", "é" * 8193, "\ud800"])
@respx.mock
async def test_invalid_body_is_rejected_before_http(server, body):
    result = await server._tool_manager.call_tool("divoid_reply_message", {"message_id": 7, "body": body})

    assert result["isError"]
    assert not respx.calls


@respx.mock
async def test_utf8_limit_is_in_bytes_and_accepts_exact_boundary(server):
    route = respx.post("http://divoid.test/messages/7/reply").mock(
        return_value=httpx.Response(200, json={"messageId": 7, "state": "sent"})
    )

    result = await server._tool_manager.call_tool("divoid_reply_message", {"message_id": 7, "body": "é" * 8192})

    assert route.call_count == 1
    assert result["state"] == "sent"


@pytest.mark.parametrize("status", [401, 403, 404, 409, 502, 503])
@respx.mock
async def test_http_errors_preserve_status_and_never_retry(server, status):
    route = respx.post("http://divoid.test/messages/7/reply").mock(
        return_value=httpx.Response(status, json={"code": "reply_outcome_unknown", "text": "Inspect status; do not resend."})
    )

    result = await server._tool_manager.call_tool("divoid_reply_message", {"message_id": 7, "body": "reply"})

    assert route.call_count == 1
    assert result["isError"]
    assert result["http_status"] == status
    if status >= 500:
        assert "do not resend automatically" in str(result["content"])


@respx.mock
async def test_timeout_declares_uncertainty_without_retry(server):
    route = respx.post("http://divoid.test/messages/7/reply").mock(side_effect=httpx.ReadTimeout("timeout"))

    result = await server._tool_manager.call_tool("divoid_reply_message", {"message_id": 7, "body": "reply"})

    assert route.call_count == 1
    assert result["isError"]
    assert "outcome may be unknown" in str(result["content"])
    assert "do not resend automatically" in str(result["content"])


@pytest.mark.parametrize("state", ["pending", "sending", "unknown", "failed", "sent"])
@respx.mock
async def test_status_is_returned_without_interpretation_or_send(server, state):
    route = respx.get("http://divoid.test/messages/7/reply").mock(
        return_value=httpx.Response(200, json={"messageId": 7, "transport": "teams", "state": state})
    )

    result = await server._tool_manager.call_tool("divoid_get_message_reply", {"message_id": 7})

    assert result["state"] == state
    assert route.call_count == 1
    assert len(respx.calls) == 1


@respx.mock
async def test_inbox_requests_body_and_transport_for_external_reply(server):
    message = {"id": 7, "body": "External Teams text", "replyTransport": "teams"}
    route = respx.get("http://divoid.test/messages").mock(
        return_value=httpx.Response(200, json={"result": [message], "total": 1, "continue": None})
    )

    result = await server._tool_manager.call_tool("divoid_list_messages", {})

    assert route.call_count == 1
    assert set(route.calls[0].request.url.params.get_list("fields")) == {
        "id", "authorid", "recipientid", "subject", "body", "createdat", "replytransport"
    }
    assert result["result"] == [message]
