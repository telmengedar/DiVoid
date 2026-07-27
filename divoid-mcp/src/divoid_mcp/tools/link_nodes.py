"""
divoid_link_nodes — create a link between two existing nodes.

Wraps POST /api/nodes/{source_id}/links with the target id as the body.
The DiVoid graph is undirected by default: link_nodes(a, b) == link_nodes(b, a).

Two OPTIONAL parameters, both defaulting to None/omitted, ride along as
query params on the same endpoint (DiVoid #7119 / #7120 / #7138):
  - link_type: direction semantics for a NEWLY-created edge (backend LinkType
    enum — pass the string name, e.g. "Unidirectional", "Bidirectional"; the
    backend is the authority on valid values, not enforced here — see
    divoid-mcp CLAUDE.md invariant 6).
  - context: free-text label carried on the edge, read source->target.
When neither is supplied, the request is byte-identical to before these
params existed (no query string) — strict back-compat.

Per the backend's design (#7120, D5/bug #702): if source/target are already
linked, the re-link is an idempotent no-op and any link_type/context passed
on that call are silently dropped by the backend — this tool does not try
to detect or special-case that; it just relays what the backend does.

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
Create a link between two existing nodes. By default the DiVoid graph is \
undirected — link_nodes(a, b) is identical to link_nodes(b, a). Use this for \
cross-linking (e.g. relating a documentation node to a task), for repair work \
(adding missing Tasks/Docs group links to an existing node), or as a building \
block when a composite tool doesn't cover your case. Optionally set link_type \
("Unidirectional" or "Bidirectional") to give the new edge direction semantics, \
and/or context to carry a free-text label on it (read source->target). Both \
default to today's undirected/contextless behavior when omitted. Re-linking an \
already-linked pair is safe (idempotent) but drops any link_type/context passed \
on that call — the backend only applies them when the edge is first created.\
"""


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_link_nodes(
        source_id: int,
        target_id: int,
        link_type: str | None = None,
        context: str | None = None,
    ) -> dict[str, Any]:
        """
        Create a link between two existing nodes.

        The DiVoid graph is undirected by default — source/target order is
        conventional only, not semantic, unless link_type says otherwise.

        Args:
            source_id: One of the two nodes to link. Must be a positive integer.
            target_id: The other node to link. Must be a positive integer
                       and different from source_id.
            link_type: Optional direction semantics for the new edge — the
                       backend LinkType enum's string name (e.g. "Unidirectional",
                       "Bidirectional"). Omit for the default undirected "None".
                       The backend is the authority on valid values; this tool
                       does not enforce a client-side allow-list.
            context: Optional free-text label carried on the edge, read
                     source_id -> target_id. Omit for no context (default).
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

        # Only include params the caller actually set — an empty dict collapses to
        # None below so post_json emits no query string at all (strict back-compat
        # with requests made before link_type/context existed).
        query_params: dict[str, Any] = {}
        if link_type is not None:
            query_params["linkType"] = link_type
        if context is not None:
            query_params["context"] = context

        logger.info(
            "divoid_link_nodes source=%d target=%d link_type=%s context=%r",
            source_id, target_id, link_type, context,
        )

        try:
            # The DiVoid API expects the target node id as a plain long integer body.
            result = await http_client.post_json(
                f"nodes/{source_id}/links", target_id, params=query_params or None
            )
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
