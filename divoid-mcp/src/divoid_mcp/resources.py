"""
MCP resources for divoid-mcp.

Registers five canonical DiVoid reference documents as divoid://node/{id}
resources. These are always-available reading material for agents — they
expose the same content as divoid_get_content but are surfaced as resources
so agent hosts can pre-fetch and present them without explicit tool calls.

No caching in Phase 1: every resource read goes through to the HTTP client.
Rationale: the content of these nodes evolves (especially #190 / #9), and
stale conventions are worse than a 50ms round-trip.

If the host does not support the MCP resources capability, the resources
are simply never listed or read — the tools remain fully functional. This
is the graceful degradation path documented in architecture A8 / §5.7.
"""

from __future__ import annotations

import logging

import mcp.server.fastmcp as fastmcp

from . import http_client

logger = logging.getLogger(__name__)

# The five canonical DiVoid reference documents.
# Format: (node_id, name, description)
CANONICAL_RESOURCES: list[tuple[int, str, str]] = [
    (9, "DiVoid Onboarding", "Agent onboarding: type vocabulary, retrieval modes, group conventions"),
    (190, "Hivemind Protocol", "Operating contract: query-first, file-always, work-in-the-graph"),
    (8, "DiVoid API Reference", "REST API reference for the DiVoid backend"),
    (493, "Structural Conventions", "Project/group topology, content-required types, status lifecycle"),
    (435, "Messaging System", "DiVoid messaging system design (Phase 2 relevance)"),
]


def register_resources(mcp_server: fastmcp.FastMCP) -> None:
    """
    Register all canonical DiVoid reference documents as MCP resources.

    Each resource URI is divoid://node/{id}. Content is fetched live from
    DiVoid on each read.
    """
    for node_id, name, description in CANONICAL_RESOURCES:
        _register_one(mcp_server, node_id, name, description)

    logger.info("Registered %d MCP resources (divoid://node/*).", len(CANONICAL_RESOURCES))


def _register_one(
    mcp_server: fastmcp.FastMCP,
    node_id: int,
    name: str,
    description: str,
) -> None:
    uri = f"divoid://node/{node_id}"

    @mcp_server.resource(uri, name=name, description=description, mime_type="text/markdown")
    async def _read_resource() -> str:
        result = await http_client.get(f"nodes/{node_id}/content")
        if result.ok:
            return result.body.decode("utf-8", errors="replace")
        logger.warning(
            "Failed to read resource divoid://node/%d: HTTP %d", node_id, result.status
        )
        return f"[Error: DiVoid returned HTTP {result.status} for node {node_id}]"
