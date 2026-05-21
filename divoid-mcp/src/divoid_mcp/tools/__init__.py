"""
Tool registry for divoid-mcp.

Importing this module registers all Phase 1 tools with the MCP server
instance. Call register_tools(mcp_server) from server.py bootstrap.
"""

from __future__ import annotations

import logging

import mcp.server.fastmcp as fastmcp

logger = logging.getLogger(__name__)


def register_tools(mcp_server: fastmcp.FastMCP) -> None:
    """Register all Phase 1 tools with the MCP server."""
    from .search import register as register_search
    from .get_node import register as register_get_node
    from .get_content import register as register_get_content
    from .link_nodes import register as register_link_nodes
    from .create_task import register as register_create_task
    from .create_documentation import register as register_create_documentation

    register_search(mcp_server)
    register_get_node(mcp_server)
    register_get_content(mcp_server)
    register_link_nodes(mcp_server)
    register_create_task(mcp_server)
    register_create_documentation(mcp_server)

    logger.info("Registered 6 MCP tools (Phase 1: read-side + link + composite writes).")
