"""
divoid_unlink_nodes — remove a link between two existing nodes.

Wraps DELETE /api/nodes/{source_id}/links/{target_id}.
The DiVoid graph is undirected: unlink_nodes(a, b) == unlink_nodes(b, a).

Idempotency: if the source node exists but no link between the two nodes
exists, the backend DELETE succeeds (deletes 0 rows) and this tool returns
{unlinked: true}. If the source node does not exist or is not accessible
to the caller, the backend returns 404 and this tool returns {unlinked: false}.

Architecture reference: §8.6
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
Remove a link between two existing nodes. The DiVoid graph is undirected — \
unlink_nodes(a, b) is identical to unlink_nodes(b, a). Use this for topology \
repair (removing wrongly-placed links), cleanup of stale cross-links, or as \
a building block when a composite tool doesn't cover your case. \
Idempotent: calling on a non-existent link returns {unlinked: false} rather than \
raising an error (provided both nodes exist and are accessible).\
"""


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_unlink_nodes(source_id: int, target_id: int) -> dict[str, Any]:
        """
        Remove a link between two existing nodes.

        The DiVoid graph is undirected — source/target order is
        conventional only, not semantic.

        Args:
            source_id: One of the two nodes to unlink. Must be a positive integer.
            target_id: The other node to unlink. Must be a positive integer
                       and different from source_id.
        """
        if source_id < 1:
            return {
                "isError": True,
                "content": make_error_content(
                    "divoid_bad_request", "source_id must be a positive integer."
                ),
            }
        if target_id < 1:
            return {
                "isError": True,
                "content": make_error_content(
                    "divoid_bad_request", "target_id must be a positive integer."
                ),
            }
        if source_id == target_id:
            return {
                "isError": True,
                "content": make_error_content(
                    "same_node_link",
                    f"source_id and target_id are the same ({source_id}). "
                    "A node cannot be linked to itself.",
                ),
            }

        logger.info("divoid_unlink_nodes source=%d target=%d", source_id, target_id)

        try:
            result = await http_client.delete(f"nodes/{source_id}/links/{target_id}")
        except http_client.DiVoidUnreachable as exc:
            code, msg = map_unreachable(exc, config.api_key, "divoid_unlink_nodes")
            logger.warning("divoid_unlink_nodes err=%s", code)
            return {"isError": True, "content": make_error_content(code, msg)}

        if not result.ok:
            if result.status == 404:
                logger.info(
                    "divoid_unlink_nodes source=%d target=%d not_found (node absent or inaccessible)",
                    source_id, target_id,
                )
                return {
                    "source_id": source_id,
                    "target_id": target_id,
                    "unlinked": False,
                }
            code, msg = map_http_error(
                result.status, result.body, config.api_key, "divoid_unlink_nodes"
            )
            logger.info(
                "divoid_unlink_nodes source=%d target=%d err=%s status=%d",
                source_id, target_id, code, result.status,
            )
            return {"isError": True, "content": make_error_content(code, msg)}

        logger.info("divoid_unlink_nodes source=%d target=%d ok", source_id, target_id)
        return {
            "source_id": source_id,
            "target_id": target_id,
            "unlinked": True,
        }
