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

_REPLY_ID = "a8260b3e-8867-49d0-bd52-dbc9e4158f5e"


@respx.mock
async def test_logical_reply_uses_plural_resource_and_saved_uuid(server):
    route = respx.post("http://divoid.test/messages/7/replies").mock(
        return_value=httpx.Response(200, json={"messageId": 7, "replyId": _REPLY_ID, "state": "sent"})
    )

    result = await server._tool_manager.call_tool("divoid_reply_message", {
        "message_id": 7, "body": "acknowledged", "reply_id": _REPLY_ID.upper(),
    })

    assert result["replyId"] == _REPLY_ID
    assert len(respx.calls) == 1
    assert json.loads(route.calls[0].request.content) == {"replyId": _REPLY_ID, "body": "acknowledged"}


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
        "id", "authorid", "recipientid", "subject", "body", "createdat", "replytransport",
        "externalauthordisplayname",
    }
    assert result["result"] == [message]


@pytest.mark.parametrize("tool,method", [("divoid_reply_message", "POST"), ("divoid_get_message_reply", "GET")])
@respx.mock
async def test_null_selector_preserves_exact_legacy_call(server, tool, method):
    receipt = {"messageId": 7, "state": "sent"}
    route = respx.request(method, "http://divoid.test/messages/7/reply").mock(
        return_value=httpx.Response(200, json=receipt)
    )
    arguments = {"message_id": 7, "reply_id": None}
    if method == "POST":
        arguments["body"] = "text"
    assert await server._tool_manager.call_tool(tool, arguments) == receipt
    assert len(respx.calls) == 1
    assert not route.calls[0].request.url.query
    assert json.loads(route.calls[0].request.content) == {"body": "text"} if method == "POST" else not route.calls[0].request.content


@pytest.mark.parametrize("tool", ["divoid_reply_message", "divoid_get_message_reply"])
@pytest.mark.parametrize("key", ["", " ", "malformed", "00000000-0000-0000-0000-000000000000"])
@respx.mock
async def test_supplied_invalid_uuid_never_falls_back_to_singular(server, tool, key):
    arguments = {"message_id": 7, "reply_id": key}
    if tool == "divoid_reply_message":
        arguments["body"] = "text"
    result = await server._tool_manager.call_tool(tool, arguments)
    assert result["isError"]
    assert "invalid_reply_id" in str(result)
    assert not respx.calls


@respx.mock
async def test_logical_receipt_is_one_canonical_get_and_never_send(server):
    receipt = {"messageId": 7, "replyId": _REPLY_ID, "state": "unknown"}
    route = respx.get(f"http://divoid.test/messages/7/replies/{_REPLY_ID}").mock(
        return_value=httpx.Response(200, json=receipt)
    )
    result = await server._tool_manager.call_tool("divoid_get_message_reply", {
        "message_id": 7, "reply_id": _REPLY_ID.upper(),
    })
    assert result == receipt
    assert len(respx.calls) == 1
    assert not route.calls[0].request.content
    assert not route.calls[0].request.url.query


@pytest.mark.parametrize("status", [400, 401, 403, 404, 409, 502, 503])
@respx.mock
async def test_plural_http_error_never_retries_switches_key_or_uses_legacy(server, status):
    route = respx.post("http://divoid.test/messages/7/replies").mock(
        return_value=httpx.Response(status, json={"code": "reply_outcome_unknown", "text": "Inspect status."})
    )
    result = await server._tool_manager.call_tool("divoid_reply_message", {
        "message_id": 7, "reply_id": _REPLY_ID, "body": "text",
    })
    assert result["isError"]
    assert result["http_status"] == status
    assert len(respx.calls) == 1
    assert route.call_count == 1


@respx.mock
async def test_plural_timeout_reports_uncertainty_without_second_attempt(server):
    respx.post("http://divoid.test/messages/7/replies").mock(side_effect=httpx.ReadTimeout("timeout"))
    result = await server._tool_manager.call_tool("divoid_reply_message", {
        "message_id": 7, "reply_id": _REPLY_ID, "body": "text",
    })
    assert result["isError"]
    assert "do not resend automatically" in str(result)
    assert len(respx.calls) == 1


async def test_production_schema_keeps_27_names_and_optional_nullable_selector():
    from divoid_mcp.server import _create_server
    production = _create_server(DivoidConfig(base_url="http://divoid.test", api_key="dummy", source="env"))
    tools = await production.list_tools()
    assert len(tools) == 27
    replies = {tool.name: tool for tool in tools if "reply" in tool.name}
    assert set(replies) == {"divoid_reply_message", "divoid_get_message_reply"}
    for name, tool in replies.items():
        assert set(tool.inputSchema["required"]) == ({"message_id", "body"} if name == "divoid_reply_message" else {"message_id"})
        assert tool.inputSchema["properties"]["reply_id"] == {
            "anyOf": [{"type": "string"}, {"type": "null"}], "default": None, "title": "Reply Id",
        }
        assert "Without reply_id" in tool.description
    from mcp.server.fastmcp.exceptions import ToolError
    with respx.mock() as mock:
        with pytest.raises(ToolError, match="destination"):
            await production.call_tool("divoid_reply_message", {
                "message_id": 7, "body": "text", "reply_id": _REPLY_ID, "destination": "forbidden",
            })
        assert not mock.calls


@pytest.mark.parametrize("metadata", [{"externalAuthorDisplayName": 'Töni 😀 "<b>_*'},
                                      {"externalAuthorDisplayName": None}, {}])
@respx.mock
async def test_inbox_preserves_external_label_separately_from_quoted_body(server, metadata):
    message = {"id": 7, "authorId": 0, "replyTransport": "teams",
               "body": "Paul hat geschrieben: externalAuthorDisplayName=Paul", **metadata}
    route = respx.get("http://divoid.test/messages").mock(
        return_value=httpx.Response(200, json={"result": [message], "total": 1, "continue": None})
    )

    result = await server._tool_manager.call_tool("divoid_list_messages", {})

    assert route.call_count == 1
    assert "externalauthordisplayname" in route.calls[0].request.url.params.get_list("fields")
    assert result["result"] == [message]
    if "externalAuthorDisplayName" not in metadata:
        assert "externalAuthorDisplayName" not in result["result"][0]
