"""
divoid_get_node — fetch a single node's properties by id.

Wraps GET /api/nodes/{id}. Returns id, type, name, status, contentType,
x, y, access, ownerId, created, lastUpdate. Does NOT return the content body —
use divoid_get_content for that.

Architecture reference: §8.2
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
Fetch a single node's properties (id, type, name, status, severity, contentType, \
position, access, ownerId, created, lastUpdate). Use this when you have a node \
id (from search results, a link, a memory pointer) and need its metadata. For \
the content body, use divoid_get_content separately — properties and content are \
intentionally split because content can be large and is not always needed.\
"""


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_get_node(id: int) -> dict[str, Any]:
        """
        Fetch a single node's properties by id.

        Args:
            id: The node id. Must be a positive integer. Returns id, type, name,
                status, severity, contentType, x, y, access, ownerId, created, lastUpdate.
        """
        if id < 1:
            return {
                "isError": True,
                "content": make_error_content("divoid_bad_request", "id must be a positive integer."),
            }

        logger.info("divoid_get_node id=%d", id)

        try:
            result = await http_client.get(f"nodes/{id}")
        except http_client.DiVoidUnreachable as exc:
            code, msg = map_unreachable(exc, config.api_key, "divoid_get_node")
            logger.warning("divoid_get_node id=%d err=%s", id, code)
            return {"isError": True, "content": make_error_content(code, msg)}

        if not result.ok:
            code, msg = map_http_error(result.status, result.body, config.api_key, "divoid_get_node")
            logger.info("divoid_get_node id=%d err=%s status=%d", id, code, result.status)
            return {"isError": True, "content": make_error_content(code, msg)}

        data = result.json()
        logger.info("divoid_get_node id=%d ok type=%s name=%r", id, data.get("type"), data.get("name"))

        return {
            "id": data.get("id"),
            "type": data.get("type"),
            "name": data.get("name"),
            "status": data.get("status"),
            "severity": data.get("severity"),
            "contentType": data.get("contentType"),
            "x": data.get("x"),
            "y": data.get("y"),
            "access": data.get("access"),
            "ownerId": data.get("ownerId"),
            "created": data.get("created"),
            "lastUpdate": data.get("lastUpdate"),
        }
