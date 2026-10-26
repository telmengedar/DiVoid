"""Node subscription management through the DiVoid hook API."""

from __future__ import annotations

from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import make_error_content, map_http_error, map_unreachable


_EVENT_FLAGS = {"status": 1, "content": 2, "links": 4, "metadata": 8}


def _encode_events(events: list[str] | None) -> tuple[int | None, str | None]:
    if events is None:
        return None, None
    if not events or any(name.strip().lower() not in _EVENT_FLAGS for name in events):
        return None, "events must be a non-empty list of: Status, Content, Links, Metadata."
    mask = 0
    for name in events:
        mask |= _EVENT_FLAGS[name.strip().lower()]
    return mask, None


def _validate(node_id: int | None, user_id: int | None, tag: str | None) -> str | None:
    for name, value in (("node_id", node_id), ("user_id", user_id)):
        if value is not None and (isinstance(value, bool) or not isinstance(value, int) or value <= 0):
            return f"{name} must be a positive integer."
    if tag is not None and (
        not tag.strip() or len(tag.strip()) > 128 or any(c in tag.strip() for c in "[]\r\n")
    ):
        return "tag must contain 1–128 characters without brackets or newlines."
    return None


async def _execute(
    method: str,
    node_id: int | None,
    user_id: int | None,
    tag: str | None,
    config: DivoidConfig,
    events: list[str] | None = None,
) -> dict[str, Any]:
    invalid = _validate(node_id, user_id, tag)
    mask, invalid_events = _encode_events(events)
    invalid = invalid or invalid_events
    if invalid:
        return {"isError": True, "content": make_error_content("invalid_hook", invalid)}
    payload: dict[str, Any] = {}
    if node_id is not None:
        payload["nodeId"] = node_id
    if user_id is not None:
        payload["userId"] = user_id
    if tag is not None:
        payload["tag"] = tag.strip()
    if mask is not None:
        payload["events"] = mask
    operation = f"{method.lower()} node hook"
    try:
        if method == "GET":
            response = await http_client.get("nodehooks", params=payload)
        elif method == "POST":
            response = await http_client.post_json("nodehooks", payload)
        else:
            response = await http_client.delete("nodehooks", body=payload)
    except http_client.DiVoidUnreachable as exc:
        code, message = map_unreachable(exc, config.api_key, operation)
        return {"isError": True, "content": make_error_content(code, message)}
    if not response.ok:
        code, message = map_http_error(response.status, response.body, config.api_key, operation)
        return {"isError": True, "content": make_error_content(code, message)}
    if method == "DELETE":
        return {"success": True, **payload}
    data = response.json()
    return {"result": data} if method == "GET" else data


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=(
        "Subscribe a user to changes of a readable node. Idempotent for node_id/user_id/tag. "
        "events optionally limits notifications to a list of Status, Content, Links, Metadata "
        "(Metadata is every other node field); omitted means all changes. "
        "Re-registering the same node_id/user_id/tag replaces its events. "
        "Deletion and loss of read access are always delivered. "
        "Messages use system authorId 0 and '[tag] Node #N changed' subjects. "
        "Non-admin callers may subscribe only themselves; tag is opaque routing text."
    ))
    async def divoid_register_node_hook(
        node_id: int, user_id: int, tag: str, events: list[str] | None = None,
    ) -> dict[str, Any]:
        return await _execute("POST", node_id, user_id, tag, config, events)

    @mcp_server.tool(description=(
        "List node subscriptions. Optional node_id narrows the list; user_id defaults to caller. "
        "Only admins may list another user's hooks. Hooks remain listed after access is revoked."
    ))
    async def divoid_list_node_hooks(
        node_id: int | None = None, user_id: int | None = None,
    ) -> dict[str, Any]:
        return await _execute("GET", node_id, user_id, None, config)

    @mcp_server.tool(description=(
        "Remove the exact node_id/user_id/tag subscription; succeeds if already absent. "
        "Only admins may remove another user's hooks. Works after node access is revoked."
    ))
    async def divoid_delete_node_hook(node_id: int, user_id: int, tag: str) -> dict[str, Any]:
        return await _execute("DELETE", node_id, user_id, tag, config)
