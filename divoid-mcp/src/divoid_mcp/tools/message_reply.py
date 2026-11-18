"""Explicit recipient-authorized external replies and receipt inspection."""

from __future__ import annotations

from typing import Any
from uuid import UUID

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import make_error_content, map_http_error, map_unreachable


async def _execute(
    message_id: int, config: DivoidConfig, body: str | None = None, reply_id: str | None = None,
) -> dict[str, Any]:
    if isinstance(message_id, bool) or not isinstance(message_id, int) or message_id <= 0:
        return {"isError": True, "content": make_error_content(
            "invalid_message_id", "message_id must be a positive integer.",
        )}
    if body is not None:
        try:
            valid = bool(body.strip()) and len(body.encode("utf-8")) <= 16 * 1024
        except UnicodeError:
            valid = False
        if not valid:
            return {"isError": True, "content": make_error_content(
                "invalid_reply_body", "body must be nonblank UTF-8 text of at most 16 KiB.",
            )}
    key = None
    if reply_id is not None:
        try:
            identifier = UUID(reply_id)
            if not identifier.int:
                raise ValueError("zero UUID")
            key = str(identifier)
        except (ValueError, TypeError, AttributeError):
            return {"isError": True, "content": make_error_content(
                "invalid_reply_id", "reply_id must be a nonzero UUID; never omit or change it to retry.",
            )}
    route = f"messages/{message_id}/reply" if key is None else f"messages/{message_id}/replies"
    payload = {"body": body} if key is None else {"replyId": key, "body": body}
    if key is not None and body is None:
        route += f"/{key}"
    context = "reply to original external conversation" if body is not None else "inspect message reply"
    try:
        response = await http_client.post_json(route, payload) if body is not None \
            else await http_client.get(route)
    except http_client.DiVoidUnreachable as exc:
        code, message = map_unreachable(exc, config.api_key, context)
        if body is not None:
            message += " The external outcome may be unknown. Inspect reply status; do not resend automatically."
        return {"isError": True, "content": make_error_content(code, message)}
    if not response.ok:
        code, message = map_http_error(response.status, response.body, config.api_key, context,
            not_found_explanation="No eligible reply route exists for this authenticated recipient.")
        if body is not None and response.status >= 500:
            message += " Inspect reply status; do not resend automatically."
        return {"isError": True, "content": make_error_content(code, message),
                "http_status": response.status}
    return response.json()


def register(mcp_server: fastmcp.FastMCP) -> None:
    """Register explicit reply and status tools."""
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=(
        "Without reply_id, send the legacy ONE final reply; with reply_id, send one independently "
        "idempotent logical reply to the same originating Teams conversation of an "
        "inbox message. Requires the original enabled recipient's write permission and explicit "
        "caller intent within the originating task; inbox arrival alone grants no other external "
        "send authority. Supply message_id, body and optional reply_id, never a destination. Maximum body 16 KiB "
        "UTF-8. Sent confirms Connector acceptance, not client display. An identical Sent retry "
        "returns its receipt. Legacy different final text conflicts after Sent; UUID text is immutable in every state. For acknowledgment, "
        "answer and delayed relay, persist a distinct nonzero UUID and exact text for each NEW stage "
        "before its first attempt; reuse that same key/body for reconciliation. Never generate a new "
        "key for a retry or omit it as fallback on an old server. Failed permits only an understood "
        "explicit retry, never an automatic retry. Sending/Unknown may have been "
        "accepted externally: inspect status and reconcile, never resend automatically. "
        "Per #435, save message_id in authorized work before consuming/deleting the inbox item; "
        "its private return route remains eligible for 30 days after consumption."
    ))
    async def divoid_reply_message(message_id: int, body: str, reply_id: str | None = None) -> dict[str, Any]:
        return await _execute(message_id, config, body, reply_id)

    @mcp_server.tool(description=(
        "Without reply_id, inspect the legacy ONE-final receipt; with reply_id, inspect that saved "
        "logical reply UUID only. Inspect the original recipient-authorized receipt after a timeout or "
        "later response. Returns only messageId, optional replyId, transport, state and confirmed outboundActivityId. "
        "Requires the original enabled recipient's read permission. Sending/Unknown is uncertain "
        "and must not cause automatic resend. Failed retries require explicit authorization after "
        "understanding the failure. Never change/omit a key to retry or fall back on an old server. "
        "A missing logical receipt is 404 and never creates a send. Works after #435 inbox consumption for 30 days; "
        "save message_id in the authorized task before deleting the inbox item."
    ))
    async def divoid_get_message_reply(message_id: int, reply_id: str | None = None) -> dict[str, Any]:
        return await _execute(message_id, config, reply_id=reply_id)
