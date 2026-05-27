"""
divoid_search — semantic search over the DiVoid graph.

Wraps GET /api/nodes?query=<text> with optional type/linkedto/status/count
filters. The DiVoid API performs vector similarity search when a query
parameter is supplied; results are returned ranked by similarity score.

Architecture reference: §8.1
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
Semantic search over the DiVoid graph. Use this when you have a question in \
natural language and don't know which node holds the answer — e.g., \
"how does X work", "what's the convention for Y", "is there documentation on Z". \
Returns a ranked list of nodes with similarity scores (0..1). Higher score = \
better match. Treat scores > 0.7 as almost certainly relevant, 0.5–0.7 as \
probably relevant, < 0.4 as no useful hit. Compose with type, linkedto, or \
status filters to narrow scope when you already know the structural shape. \
Always prefer this over divoid_list for question-shaped queries.

Return shape: each result has id, name, similarity, and optionally type, \
status, and contentType. type is null for structural group nodes (Tasks, Docs \
containers); status is null for nodes whose type does not carry a lifecycle \
(most types other than task / bug). Use n.get() rather than direct key access \
when consuming results. Set include_content=True to fetch the body inline on \
each row — opt-in for research / lookup flows that need to read the bodies of \
the top hits; costs bandwidth. Set include_links=True to fetch direct neighbor \
ids inline on each row — opt-in for graph-walking / fan-out-avoidance flows; \
costs bandwidth proportional to adjacency density.\
"""


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_search(
        query: str,
        type: list[str] | None = None,
        linkedto: list[int] | None = None,
        status: list[str] | None = None,
        count: int = 10,
        include_content: bool = False,
        include_links: bool = False,
    ) -> dict[str, Any]:
        """
        Semantic search over the DiVoid graph.

        Args:
            query: The question or topic, in plain language.
                   Searched semantically against node content + name.
                   Min 1 char, max 1000 chars.
            type: Optional filter: only return nodes of these types
                  (e.g. ['task', 'documentation']).
            linkedto: Optional filter: only return nodes linked to any
                      of these node ids (both link directions).
            status: Optional filter: only return nodes with one of these
                    statuses (e.g. ['open', 'in-progress']).
            count: Number of results to return. Minimum 1, maximum 50.
                   Default 10. Capped lower than the DiVoid API cap (500)
                   because semantic results past the top-50 are almost
                   never useful and inflate context.
            include_content: If true, fetch the body inline on each row. Text content arrives
                             as a UTF-8 string; binary content arrives as a base64 string.
                             Nodes with no content omit the field. Opt-in; costs bandwidth.
            include_links: If true, fetch direct neighbor ids inline on each row. Returns
                           links: [id, ...] (or [] for isolated nodes). Use for graph-walking /
                           fan-out-avoidance flows. Opt-in; costs bandwidth proportional to
                           adjacency density.
        """
        if not query or not query.strip():
            return {
                "isError": True,
                "content": make_error_content(
                    "divoid_bad_request", "query must not be empty."
                ),
            }

        if len(query) > 1000:
            return {
                "isError": True,
                "content": make_error_content(
                    "divoid_bad_request", "query must be 1000 characters or fewer."
                ),
            }

        count = max(1, min(50, count))

        params: dict[str, Any] = {
            "query": query,
            "count": count,
        }
        if type:
            params["type"] = type
        if linkedto:
            params["linkedto"] = linkedto
        if status:
            params["status"] = status
        if include_content or include_links:
            base_fields = ["id", "type", "name", "status", "contentType", "similarity"]
            if include_content:
                base_fields.append("content")
            if include_links:
                base_fields.append("links")
            params["fields"] = base_fields

        logger.info(
            "divoid_search query=%r type=%s linkedto=%s status=%s count=%d",
            query[:80] + ("..." if len(query) > 80 else ""),
            type,
            linkedto,
            status,
            count,
        )

        try:
            result = await http_client.get("nodes", params=params)
        except http_client.DiVoidUnreachable as exc:
            code, msg = map_unreachable(exc, config.api_key, "divoid_search")
            logger.warning("divoid_search err=%s", code)
            return {"isError": True, "content": make_error_content(code, msg)}

        if not result.ok:
            code, msg = map_http_error(result.status, result.body, config.api_key, "divoid_search")
            logger.info("divoid_search err=%s status=%d", code, result.status)
            return {"isError": True, "content": make_error_content(code, msg)}

        data = result.json()
        raw_results = data.get("result", [])
        total = data.get("total", len(raw_results))

        nodes = []
        for n in raw_results:
            row: dict[str, Any] = {
                "id": n.get("id"),
                "type": n.get("type"),
                "name": n.get("name"),
                "status": n.get("status"),
                "similarity": n.get("similarity"),
            }
            if "contentType" in n:
                row["contentType"] = n["contentType"]
            if "content" in n:
                row["content"] = n["content"]
            if "links" in n:
                row["links"] = n["links"]
            nodes.append(row)

        logger.info("divoid_search ok total=%d returned=%d", total, len(nodes))
        return {"results": nodes, "total": total}
