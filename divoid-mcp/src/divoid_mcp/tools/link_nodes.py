"""
divoid_link_nodes — create a link between two existing nodes.

Wraps POST /api/nodes/{source_id}/links with the target id as the body.
The DiVoid graph is undirected: link_nodes(a, b) == link_nodes(b, a).

The DiVoid API handles duplicate links idempotently (200 OK on re-link).
Re-linking is safe — no pre-check required.

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
Create a link between two existing nodes. The DiVoid graph is undirected — \
link_nodes(a, b) is identical to link_nodes(b, a). Use this for cross-linking \
(e.g. relating a documentation node to a task), for repair work (adding missing \
Tasks/Docs group links to an existing node), or as a building block when a \
composite tool doesn't cover your case. Re-linking is safe (idempotent).\
"""


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_link_nodes(source_id: int, target_id: int) -> dict[str, Any]:
        """
        Create a link between two existing nodes.

        The DiVoid graph is undirected — source/target order is
        conventional only, not semantic.

        Args:
            source_id: One of the two nodes to link. Must be a positive integer.
            target_id: The other node to link. Must be a positive integer
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

        logger.info("divoid_link_nodes source=%d target=%d", source_id, target_id)

        try:
            # The DiVoid API expects the target node id as a plain long integer body.
            result = await http_client.post_json(f"nodes/{source_id}/links", target_id)
        except http_client.DiVoidUnreachable as exc:
            code, msg = map_unreachable(exc, config.api_key, "divoid_link_nodes")
            logger.warning("divoid_link_nodes err=%s", code)
            return {"isError": True, "content": make_error_content(code, msg)}

        if not result.ok:
            code, msg = map_http_error(
                result.status, result.body, config.api_key, "divoid_link_nodes"
            )
            logger.info(
                "divoid_link_nodes source=%d target=%d err=%s status=%d",
                source_id, target_id, code, result.status,
            )
            return {"isError": True, "content": make_error_content(code, msg)}

        logger.info("divoid_link_nodes source=%d target=%d ok", source_id, target_id)
        return {
            "source_id": source_id,
            "target_id": target_id,
            "linked": True,
        }
