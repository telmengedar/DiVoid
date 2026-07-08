"""
divoid_delete_node -- primitive: permanently delete a node from the graph.

Thin wrapper around DELETE /api/nodes/{id}.

**This operation is destructive and irreversible.** Deleting a node:
  - Removes the node itself.
  - Removes all links the node participates in (in either direction).
  - Can orphan neighbouring nodes that had the deleted node as their only
    connection to the rest of the graph (DiVoid #493 §7: "do not delete
    unless you mean it; prefer superseding an old node over deleting it").

There is no soft-delete, archive, or recycle-bin. There is no interactive
confirmation (MCP provides none). This tool exists for REST parity — if the
REST endpoint accepts it, so does this tool.

Auth rules (server-enforced):
  - Node owner or admin can delete.
  - 404 if the node does not exist (or caller has no right to know it does).
  - 2xx (typically 204 No Content) on success.

Error shapes:
  - 404: node does not exist.
  - 403: caller does not have permission to delete this node.
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
Permanently delete a DiVoid node by id. Maps to DELETE /api/nodes/{id}. \
DESTRUCTIVE AND IRREVERSIBLE: deleting a node also removes all its links, \
which can orphan neighbouring nodes (DiVoid #493 §7 — prefer superseding \
an old node over deleting it; delete only when you are certain). \
There is no soft-delete or confirmation step — this tool exists for REST \
parity. \
Returns {success: true, id: N} on success; isError on 404/403/5xx.\
"""


async def _execute(id: int, config: "DivoidConfig") -> dict[str, Any]:
    """
    Core implementation of divoid_delete_node.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.
    """
    logger.info("divoid_delete_node id=%d", id)

    try:
        result = await http_client.delete(f"nodes/{id}")
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, "delete node")
        logger.warning("divoid_delete_node err=%s", code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not result.ok:
        code, msg = map_http_error(
            result.status, result.body, config.api_key, "delete node"
        )
        logger.info("divoid_delete_node err=%s status=%d", code, result.status)
        return {"isError": True, "content": make_error_content(code, msg)}

    logger.info("divoid_delete_node id=%d deleted", id)
    return {"success": True, "id": id}


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_delete_node(id: int) -> dict[str, Any]:
        """
        Permanently delete a DiVoid node by id.

        DESTRUCTIVE: the node, all its links, and any content blob are
        removed immediately and cannot be recovered. Neighbouring nodes
        that were only reachable through this node become orphaned.
        Prefer superseding over deleting (DiVoid #493 §7).

        Args:
            id: The integer id of the node to delete. Must be a positive
                integer. Returned in the 'id' field of search/list/get results.
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
