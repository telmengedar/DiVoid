"""
divoid_delete_message -- primitive: delete a message from the inbox.

Thin wrapper around DELETE /api/messages/{id}.

Per DiVoid #435 lifecycle: read -> act -> **delete**. There is no archive.
Every message an agent acts on must be deleted when done. An undeleted
read message is a bug — it will be re-processed by the next inbox scan.

Auth rules (server-enforced, per DiVoid #426 §4):
  - Only the RECIPIENT of a message can delete it.
  - Admins can delete any message regardless of who the recipient is.
  - Senders cannot recall: a 403 is returned if a non-recipient, non-admin
    tries to delete (existence is not revealed — server returns 403, not 404,
    to prevent probing).
  - In this deployment, the admin API key authenticates as Selene (user_id=2),
    who is also the canonical recipient for self-messages, so the admin key
    can delete any message it is the recipient of OR any message at all.

Error shapes:
  - 404: message does not exist (or: exists but caller has no right to know).
  - 403: caller is not the recipient and not an admin.
  - 2xx (typically 204 No Content): success.
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
Delete a DiVoid message by id. Maps to DELETE /api/messages/{id}. \
This is the REQUIRED end-of-lifecycle step per DiVoid #435: \
read -> act -> delete. There is no archive; an undeleted message is a bug \
and will be re-processed by every future inbox scan. \
Auth: only the recipient or an admin can delete. Senders cannot recall — \
a 403 is returned for non-recipients (the no-recall guarantee is structural, \
not just policy). In this deployment the admin API key (Selene, user_id=2) \
can delete any message it received or any message at all (admin right). \
Returns {success: true, id: N} on success; isError on 404/403/5xx.\
"""


async def _execute(id: int, config: "DivoidConfig") -> dict[str, Any]:
    """
    Core implementation of divoid_delete_message.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.
    """
    logger.info("divoid_delete_message id=%d", id)

    try:
        result = await http_client.delete(f"messages/{id}")
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, "delete message")
        logger.warning("divoid_delete_message err=%s", code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not result.ok:
        code, msg = map_http_error(
            result.status, result.body, config.api_key, "delete message"
        )
        logger.info("divoid_delete_message err=%s status=%d", code, result.status)
        return {"isError": True, "content": make_error_content(code, msg)}

    logger.info("divoid_delete_message id=%d deleted", id)
    return {"success": True, "id": id}


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_delete_message(id: int) -> dict[str, Any]:
        """
        Delete a DiVoid message by id (end-of-lifecycle step per #435).

        Args:
            id: The integer id of the message to delete. Must be a positive
                integer. Returned in the 'id' field of divoid_list_messages
                result items and in the send response.
        """
        if not isinstance(id, int) or id <= 0:
            return {
                "isError": True,
                "content": make_error_content(
                    "invalid_id",
                    f"id must be a positive integer, got {id!r}.",
                ),
            }
        return await _execute(id=id, config=config)
