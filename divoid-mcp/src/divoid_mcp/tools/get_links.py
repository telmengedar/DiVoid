"""
divoid_get_links -- primitive wrapper around GET /api/nodes/links.

Returns link adjacency rows where either endpoint is in the supplied id set.
This is the efficient way to fetch all edges incident to a set of visible nodes
in one round-trip, rather than querying linkedto= for each node individually.

Response shape mirrors the API:
  {"result": [{"source_id": int, "target_id": int}, ...], "total": int, "continue": int | null}

The graph is undirected — both source_id and target_id can be either endpoint.
Links are returned in NodeLink storage order.

Invariant guards (before any HTTP call):
  - ids must be non-empty → ids_empty

API reference: DiVoid #8 (GET /api/nodes/links).
Architecture reference: DiVoid #695 §Tool 4 (get_links primitive).
"""

from __future__ import annotations

import logging
from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import InvariantViolation, make_error_content, map_http_error, map_unreachable

logger = logging.getLogger(__name__)

_TOOL_DESCRIPTION = """\
Fetch link adjacency rows for a set of node ids. Returns all edges where either \
endpoint is in the supplied id list — one round-trip for N nodes. Use this to \
load the neighbourhood of a known node set (e.g. all edges incident to the nodes \
returned by a divoid_list call). The graph is undirected: both source_id and \
target_id can be any endpoint. At least one id must be provided (invariant guard: \
ids_empty). count defaults to 500 (the API max); use continue_cursor to paginate \
if a node is heavily connected.\
"""


def _check_invariants(ids: list[int]) -> None:
    """
    Check runtime invariants before making any HTTP call.

    Raises InvariantViolation with a stable code if any invariant is broken.
    The invariant guard is the sole enforcement layer — FastMCP exposes ids
    as a plain list parameter without minItems constraints; enforcement is
    entirely here.
    """
    if not ids:
        raise InvariantViolation(
            "ids_empty",
            "ids must contain at least one node id. "
            "An empty ids list would return all links in the graph (unbounded).",
        )


async def _execute(
    ids: list[int],
    config: "DivoidConfig",
    count: int = 500,
    continue_cursor: int | None = None,
) -> dict[str, Any]:
    """
    Core implementation of divoid_get_links.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.

    Callers must run _check_invariants() before calling this function.
    """
    # Clamp count to API max.
    count = max(1, min(500, count))

    params: dict[str, Any] = {
        "ids": ids,
        "count": count,
    }
    if continue_cursor is not None:
        params["continue"] = continue_cursor

    logger.info("divoid_get_links ids=%s count=%d", ids, count)

    try:
        result = await http_client.get("nodes/links", params=params)
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, "GET nodes/links")
        logger.warning("divoid_get_links err=%s", code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not result.ok:
        code, msg = map_http_error(result.status, result.body, config.api_key, "GET nodes/links")
        logger.info("divoid_get_links err=%s status=%d", code, result.status)
        return {"isError": True, "content": make_error_content(code, msg)}

    try:
        data = result.json()
    except Exception as exc:
        return {
            "isError": True,
            "content": make_error_content(
                "divoid_bad_request",
                f"GET nodes/links: Could not parse response: {exc}",
            ),
        }

    # Normalise camelCase API keys to snake_case for consistency with the
    # rest of the divoid-mcp response shape.
    raw_links = data.get("result", [])
    normalised = [
        {
            "source_id": link.get("sourceId"),
            "target_id": link.get("targetId"),
        }
        for link in raw_links
    ]

    total = data.get("total", len(normalised))
    continue_val = data.get("continue", None)

    logger.info("divoid_get_links ok total=%d returned=%d", total, len(normalised))

    return {
        "result": normalised,
        "total": total,
        "continue": continue_val,
    }


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_get_links(
        ids: list[int],
        count: int = 500,
        continue_cursor: int | None = None,
    ) -> dict[str, Any]:
        """
        Fetch link adjacency rows incident to the supplied node ids.

        Args:
            ids: Node ids whose incident links are requested (required, min 1 id).
                 Accepts a list of integers. The invariant guard fires with
                 ids_empty if the list is empty — FastMCP does not enforce
                 minItems in JSON Schema; enforcement is entirely here.
            count: Page size (max 500, default 500). Silently clamped.
            continue_cursor: Pagination cursor from a previous response's
                             'continue' field. Null or absent = first page.
        """
        # --- Invariant guard (before any HTTP call) ---
        try:
            _check_invariants(ids)
        except InvariantViolation as exc:
            logger.debug("divoid_get_links invariant violation: %s", exc.code)
            return {"isError": True, "content": make_error_content(exc.code, exc.message)}

        return await _execute(
            ids=ids,
            config=config,
            count=count,
            continue_cursor=continue_cursor,
        )
