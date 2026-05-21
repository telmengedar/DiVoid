"""
Tool registry for divoid-mcp.

Importing this module registers all tools with the MCP server instance.
Call register_tools(mcp_server) from server.py bootstrap.
"""

from __future__ import annotations

import logging

import mcp.server.fastmcp as fastmcp

logger = logging.getLogger(__name__)


def register_tools(mcp_server: fastmcp.FastMCP) -> None:
    """Register all tools with the MCP server."""
    from .search import register as register_search
    from .get_node import register as register_get_node
    from .get_content import register as register_get_content
    from .link_nodes import register as register_link_nodes
    from .create_task import register as register_create_task
    from .create_documentation import register as register_create_documentation
    from .create_session_log import register as register_create_session_log
    from .resolve_user import register as register_resolve_user
    from .send_message import register as register_send_message
    from .list_messages import register as register_list_messages

    register_search(mcp_server)
    register_get_node(mcp_server)
    register_get_content(mcp_server)
    register_link_nodes(mcp_server)
    register_create_task(mcp_server)
    register_create_documentation(mcp_server)
    register_create_session_log(mcp_server)
    register_resolve_user(mcp_server)
    register_send_message(mcp_server)
    register_list_messages(mcp_server)

    logger.info("Registered 10 MCP tools (Phase 1 + Phase 2: read-side + link + composite writes + messaging).")
