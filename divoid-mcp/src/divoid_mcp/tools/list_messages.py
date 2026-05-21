"""
divoid_list_messages -- primitive: list the inbox for the calling identity.

Thin wrapper around GET /api/messages with paging support.

The admin API key authenticates as the admin user (Selene, user_id=2 in this
deployment). The endpoint returns all messages where the caller is the author
OR the recipient — for the admin key that is the full inbox/outbox.

Paging follows the same {result, total, continue} shape as GET /api/nodes:
  - count:    max items per page (server clamps to <=500).
  - continue: offset token (integer) for the next page, returned in the response.

Per DiVoid #435 lifecycle: scan the inbox at task start, task end, and idle.
For each message in your project's scope: act on it, then DELETE it
(use divoid_delete_message or call DELETE /api/messages/{id} directly).
Messages NOT in your session's project scope: leave untouched.

There is no archive. An undeleted read message is a bug.
"""

from __future__ import annotations

import logging
from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import make_error_content, map_http_error, map_unreachable

logger = logging.getLogger(__name__)

_TOOL_DESCRIPTION = """\
List the DiVoid message inbox for the calling identity (Selene, user_id=2 in \
this deployment). Returns messages where the caller is author or recipient, \
paged by count/continue. \
Scan at task start, task end, and when idle — per DiVoid #435 scanning discipline. \
For each message in your current project's scope (check the subject tag): act on \
it, then delete it (there is no archive; undeleted messages are bugs). Leave \
messages outside your project scope untouched — another session will match. \
Response shape: {result: [{id, authorId, recipientId, subject, body, createdAt}], \
total, continue} where continue is the offset for the next page (null when exhausted).\
"""


async def _execute(
    count: int,
    continue_offset: int,
    config: "DivoidConfig",
) -> dict[str, Any]:
    """
    Core implementation of divoid_list_messages.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.
    """
    logger.info("divoid_list_messages count=%d continue=%d", count, continue_offset)

    params: dict[str, Any] = {"count": count}
    if continue_offset > 0:
        params["continue"] = continue_offset

    try:
        result = await http_client.get("messages", params=params)
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, "list messages")
        logger.warning("divoid_list_messages err=%s", code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not result.ok:
        code, msg = map_http_error(
            result.status, result.body, config.api_key, "list messages"
        )
        logger.info("divoid_list_messages err=%s status=%d", code, result.status)
        return {"isError": True, "content": make_error_content(code, msg)}

    try:
        data = result.json()
        messages = data.get("result", [])
        total = data.get("total", 0)
        next_continue = data.get("continue")
    except Exception as exc:
        return {
            "isError": True,
            "content": make_error_content(
                "divoid_bad_request",
                f"list messages: Could not parse response: {exc}",
            ),
        }

    logger.info(
        "divoid_list_messages count=%d total=%d next_continue=%s page_size=%d",
        count, total, next_continue, len(messages),
    )

    return {
        "result": messages,
        "total": total,
        "continue": next_continue,
    }


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_list_messages(
        count: int = 50,
        continue_offset: int = 0,
    ) -> dict[str, Any]:
        """
        List the DiVoid message inbox for the calling identity.

        Args:
            count: Maximum number of messages to return per page (default 50,
                   server clamps to <=500). Enforcement: count must be a positive
                   integer; FastMCP exposes it as plain {"type": "integer"} —
                   the invariant guard is the sole enforcement layer.
            continue_offset: Paging offset returned in a prior response's
                             'continue' field. Pass 0 (default) for the first page.
                             Enforcement: must be a non-negative integer (invariant guard).
        """
        if not isinstance(count, int) or count <= 0:
            return {
                "isError": True,
                "content": make_error_content(
                    "invalid_count",
                    f"count must be a positive integer, got {count!r}.",
                ),
            }
        if not isinstance(continue_offset, int) or continue_offset < 0:
            return {
                "isError": True,
                "content": make_error_content(
                    "invalid_continue",
                    f"continue_offset must be a non-negative integer, got {continue_offset!r}.",
                ),
            }
        return await _execute(count=count, continue_offset=continue_offset, config=config)
