"""
divoid_list -- structured listing with path-query traversal and all listing filters.

Wraps GET /api/nodes with the full filtering, paging, sorting, and path-query
surface. This is the structural retrieval tool — use it when you know the
topology (type, status, linkedto, path). Use divoid_search for semantic /
"is there anything about X" lookups.

Invariants enforced at runtime (not expressible in JSON Schema / FastMCP):
  - path and linkedto are mutually exclusive (path subsumes linkedto per #8).
  - nostatus=True and a non-empty status[] are mutually exclusive.
  - no_severity=True and a non-empty severity[] are mutually exclusive.
  - bounds must have exactly 4 elements if provided.
  - sort must be one of: id, type, name, status, severity.
  - count is clamped silently to [1, 500].

Unknown field names in the fields parameter are passed through to the API,
which returns HTTP 400 for unrecognised values. The error is surfaced via
map_http_error — no client-side guard is needed.

Timestamp filters (created_from, created_to, updated_from, updated_to) accept
ISO 8601 datetime strings and are forwarded as-is to the backend query params.
The backend semantics are From inclusive, To exclusive.

API reference: DiVoid node #8 (GET /api/nodes + path parameter section).
Architecture: DiVoid node #695.
"""

from __future__ import annotations

import logging
from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import InvariantViolation, make_error_content, map_http_error, map_unreachable

logger = logging.getLogger(__name__)

_VALID_SORT_FIELDS = frozenset({"id", "type", "name", "status", "severity"})

_TOOL_DESCRIPTION = """\
Structural listing of DiVoid nodes. Use this when you know the topology — type, \
status, linkedto, or path — and want to enumerate matching nodes. Use divoid_search \
instead when you have a plain-language question or don't know which node holds an answer.

WHEN TO USE path= vs linkedto=:
  - linkedto=[N]: single-hop — "what is directly connected to node N in either direction".
    Fast, simple, most common for project/group walks.
  - path=...: multi-hop traversal — "start from X, traverse through Y, return Z". Use
    when you need to cross more than one link boundary, e.g. reaching open tasks two
    hops from an org root:
      [type:organization,name:Pooshit]/[type:project,name:DiVoid]/[type:task,status:open]
    path and linkedto are mutually exclusive — reject if both provided.

PATH GRAMMAR (v1, ref DiVoid #8):
  path = segment ("/" segment)*
  segment = "[" predicate ("," predicate)* "]"   -- comma = AND between predicates
  predicate = key ":" valueList
  key = id | type | name | status
  valueList = value ("|" value)*                  -- pipe = OR within a key
  Examples:
    [type:project,name:DiVoid]               -- project named DiVoid
    [id:3]/[name:Tasks]/[type:task,status:open]  -- open tasks under DiVoid via Tasks group
    [type:project,name:Di%]                  -- projects whose name starts with Di (LIKE)
    [type:task,status:open|new]              -- tasks with status=open OR status=new
    [id:42]/[]                               -- anything linked to node 42

  SEED CONSTRAINT: the first segment must contain at least one predicate. An
  unconstrained [] first segment returns HTTP 400.

FILTER SEMANTICS (when no path=):
  - Multiple values within id/type/name/status are OR-within-key (ANY match).
  - Multiple keys together are AND-across-keys.
  - name and status support % and _ wildcards (SQL LIKE semantics).
  - bounds=[xMin,yMin,xMax,yMax] restricts to nodes within that viewport rectangle.
    Also applies to the terminal hop when using path=.

PAGINATION: supply the `continue` value from a previous response to fetch the next
  page. `continue` is null/absent when there are no more results. count defaults to
  20 and is capped at 500.

FIELDS: default projection is [id, type, name, status, contentType]. Add x or y to
  get canvas positions. Omit fields to reduce token footprint on large result sets.
  Set include_content=True to fetch the body inline on each row — opt-in for research /
  lookup flows; costs bandwidth proportional to the total body size of the page; for many
  small documentation nodes this saves N follow-up divoid_get_content calls.
  Set include_links=True to fetch the direct neighbor ids inline on each row — opt-in for
  graph-walking / fan-out-avoidance flows; costs bandwidth proportional to adjacency
  density; saves N follow-up divoid_get_links calls when you need the full adjacency of a
  page of nodes.

SEVERITY FILTERS:
  - severity=[3,5]: exact match — return only nodes whose severity is 3 or 5.
    Mutually exclusive with no_severity (invariant guard).
  - severity_min=2, severity_max=4: inclusive range filter — [2, 3, 4].
    Either bound may be omitted.
  - no_severity=true: return only nodes with no severity set (NULL). Mutually
    exclusive with severity[].
  - sort="severity": order by severity ascending (combine with descending=true for DESC).
    Each result row always includes severity: int | null.\
"""


def _check_invariants(
    path: str | None,
    linkedto: list[int] | None,
    nostatus: bool,
    status: list[str] | None,
    bounds: list[float] | None,
    sort: str | None,
    fields: list[str] | None,
    no_severity: bool = False,
    severity: list[int] | None = None,
    severity_min: int | None = None,
    severity_max: int | None = None,
) -> None:
    """
    Enforce runtime invariants before making any HTTP call.

    Raises InvariantViolation with a stable code if any invariant is broken.
    Enforcement is entirely at runtime — FastMCP exposes parameters as plain
    JSON Schema types without cross-parameter constraints.

    Unknown field names in the fields parameter are NOT checked here — the API
    returns HTTP 400 for unrecognised field names, which surfaces via map_http_error.
    """
    if path is not None and linkedto:
        raise InvariantViolation(
            "mutually_exclusive_path_linkedto",
            "path and linkedto are mutually exclusive (path subsumes linkedto, per DiVoid #8). "
            "Use path= for multi-hop topology walks; use linkedto= for single-hop neighbor lookups.",
        )

    if nostatus and status:
        raise InvariantViolation(
            "mutually_exclusive_nostatus_status",
            "nostatus=true returns nodes with no status set; providing status[] simultaneously "
            "is contradictory. Provide one or the other.",
        )

    if bounds is not None and len(bounds) != 4:
        raise InvariantViolation(
            "bounds_invalid_length",
            f"bounds must have exactly 4 elements [xMin, yMin, xMax, yMax], "
            f"got {len(bounds)}.",
        )

    if sort is not None and sort not in _VALID_SORT_FIELDS:
        raise InvariantViolation(
            "sort_invalid_field",
            f"sort must be one of: {', '.join(sorted(_VALID_SORT_FIELDS))}. Got {sort!r}.",
        )

    if no_severity and severity:
        raise InvariantViolation(
            "mutually_exclusive_noseverity_severity",
            "no_severity=true returns nodes with no severity set; providing severity[] "
            "simultaneously is contradictory. Provide one or the other.",
        )


_DEFAULT_FIELDS = ["id", "type", "name", "status", "contentType"]


async def _execute(
    config: "DivoidConfig",
    id: list[int] | None = None,
    type: list[str] | None = None,
    name: list[str] | None = None,
    status: list[str] | None = None,
    linkedto: list[int] | None = None,
    nostatus: bool = False,
    path: str | None = None,
    bounds: list[float] | None = None,
    count: int = 20,
    continue_cursor: int | None = None,
    sort: str | None = None,
    descending: bool = False,
    fields: list[str] | None = None,
    include_content: bool = False,
    include_links: bool = False,
    created_from: str | None = None,
    created_to: str | None = None,
    updated_from: str | None = None,
    updated_to: str | None = None,
    severity: list[int] | None = None,
    severity_min: int | None = None,
    severity_max: int | None = None,
    no_severity: bool = False,
) -> dict[str, Any]:
    """
    Core implementation of divoid_list.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.

    Callers must run _check_invariants() before calling this function.
    """
    count = max(1, min(500, count))

    if include_content or include_links:
        base_fields = list(fields) if fields is not None else list(_DEFAULT_FIELDS)
        if include_content and "content" not in base_fields:
            base_fields.append("content")
        if include_links and "links" not in base_fields:
            base_fields.append("links")
        fields = base_fields

    params: dict[str, Any] = {"count": count}

    if id:
        params["id"] = id
    if type:
        params["type"] = type
    if name:
        params["name"] = name
    if status:
        params["status"] = status
    if linkedto:
        params["linkedto"] = linkedto
    if nostatus:
        params["nostatus"] = "true"
    if path is not None:
        params["path"] = path
    if bounds is not None:
        params["bounds"] = bounds
    if continue_cursor is not None:
        params["continue"] = continue_cursor
    if sort is not None:
        params["sort"] = sort
    if descending:
        params["descending"] = "true"
    if fields is not None:
        params["fields"] = fields
    if created_from is not None:
        params["CreatedFrom"] = created_from
    if created_to is not None:
        params["CreatedTo"] = created_to
    if updated_from is not None:
        params["UpdatedFrom"] = updated_from
    if updated_to is not None:
        params["UpdatedTo"] = updated_to
    if severity:
        params["severity"] = severity
    if severity_min is not None:
        params["severityMin"] = severity_min
    if severity_max is not None:
        params["severityMax"] = severity_max
    if no_severity:
        params["noSeverity"] = "true"

    logger.info(
        "divoid_list path=%r linkedto=%s type=%s status=%s count=%d",
        path,
        linkedto,
        type,
        status,
        count,
    )

    try:
        result = await http_client.get("nodes", params=params)
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, "divoid_list")
        logger.warning("divoid_list err=%s", code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not result.ok:
        code, msg = map_http_error(result.status, result.body, config.api_key, "divoid_list")
        logger.info("divoid_list err=%s status=%d", code, result.status)
        return {"isError": True, "content": make_error_content(code, msg)}

    data = result.json()
    raw_results = data.get("result", [])
    total = data.get("total", len(raw_results))
    continue_val = data.get("continue", None)

    logger.info("divoid_list ok total=%d returned=%d continue=%s", total, len(raw_results), continue_val)

    return {
        "result": raw_results,
        "total": total,
        "continue": continue_val,
    }


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_list(
        id: list[int] | None = None,
        type: list[str] | None = None,
        name: list[str] | None = None,
        status: list[str] | None = None,
        linkedto: list[int] | None = None,
        nostatus: bool = False,
        path: str | None = None,
        bounds: list[float] | None = None,
        count: int = 20,
        continue_cursor: int | None = None,
        sort: str | None = None,
        descending: bool = False,
        fields: list[str] | None = None,
        include_content: bool = False,
        include_links: bool = False,
        created_from: str | None = None,
        created_to: str | None = None,
        updated_from: str | None = None,
        updated_to: str | None = None,
        severity: list[int] | None = None,
        severity_min: int | None = None,
        severity_max: int | None = None,
        no_severity: bool = False,
    ) -> dict[str, Any]:
        """
        List DiVoid nodes with structural filters, pagination, and optional path-query.

        Args:
            id: Filter by node id(s). Multiple values = OR.
            type: Filter by type name(s) (e.g. ['task', 'documentation']). OR within list.
                  Use divoid_search for semantic lookups; type= here is exact match.
            name: Filter by name. Supports % and _ wildcards (SQL LIKE). OR within list.
            status: Filter by status (e.g. ['open', 'in-progress']). Wildcards supported.
                    Mutually exclusive with nostatus (invariant guard, not JSON Schema).
            linkedto: Return nodes linked to any of these node ids (both link directions).
                      Single-hop. Mutually exclusive with path (invariant guard).
            nostatus: If true, return only nodes with no status set.
                      Mutually exclusive with status[] (invariant guard).
            path: Path-query expression for multi-hop traversal. Raw string, passed as-is
                  to the API. Example: '[type:project,name:DiVoid]/[type:task,status:open]'.
                  See tool description for grammar. The first segment must have at least one
                  predicate ([] first segment = HTTP 400). Mutually exclusive with linkedto.
            bounds: Viewport bounding rectangle [xMin, yMin, xMax, yMax] — returns only
                    nodes whose canvas X/Y falls inside. Must be exactly 4 values if provided
                    (invariant guard). Applies to terminal hop when combined with path.
            count: Page size. Default 20, max 500. Silently clamped; no error on out-of-range.
            continue_cursor: Pagination cursor from a previous response's 'continue' field.
                             Null or absent = first page.
            sort: Sort field: 'id', 'type', 'name', 'status', or 'severity'. Validated by invariant guard.
            descending: If true, sort descending. Default false (ascending).
            fields: Fields to include in each result node. Default: id, type, name, status,
                    contentType. Also available: x, y.
            include_content: If true, fetch the body inline on each row. Appends 'content' to
                             the fields projection (and uses the full default projection if
                             fields was not specified). Text content arrives as a UTF-8 string;
                             binary content arrives as a base64 string. Nodes with no content
                             omit the field entirely. Opt-in; costs bandwidth.
            include_links: If true, fetch direct neighbor ids inline on each row. Appends
                           'links' to the fields projection. Returns links: [id, ...] (or []
                           for isolated nodes). Use for graph-walking / fan-out-avoidance flows
                           that would otherwise issue N divoid_get_links calls. Opt-in; costs
                           bandwidth proportional to adjacency density.
            created_from: ISO 8601 datetime string. Return only nodes created at or after
                          this timestamp (inclusive). Forwarded as-is to the backend.
            created_to: ISO 8601 datetime string. Return only nodes created before this
                        timestamp (exclusive). Forwarded as-is to the backend.
            updated_from: ISO 8601 datetime string. Return only nodes last updated at or
                          after this timestamp (inclusive). Forwarded as-is to the backend.
            updated_to: ISO 8601 datetime string. Return only nodes last updated before this
                        timestamp (exclusive). Forwarded as-is to the backend.
            severity: Filter by exact severity value(s). Multiple values = OR. Mutually
                      exclusive with no_severity (invariant guard). Forwarded as ?severity=.
            severity_min: Inclusive lower bound on severity. May be combined with severity_max.
                          Forwarded as ?severityMin=.
            severity_max: Inclusive upper bound on severity. May be combined with severity_min.
                          Forwarded as ?severityMax=.
            no_severity: If true, return only nodes with no severity set (NULL). Mutually
                         exclusive with severity[] (invariant guard). Forwarded as ?noSeverity=true.
        """
        try:
            _check_invariants(
                path=path,
                linkedto=linkedto,
                nostatus=nostatus,
                status=status,
                bounds=bounds,
                sort=sort,
                fields=fields,
                no_severity=no_severity,
                severity=severity,
                severity_min=severity_min,
                severity_max=severity_max,
            )
        except InvariantViolation as exc:
            logger.debug("divoid_list invariant violation: %s", exc.code)
            return {"isError": True, "content": make_error_content(exc.code, exc.message)}

        return await _execute(
            config=config,
            id=id,
            type=type,
            name=name,
            status=status,
            linkedto=linkedto,
            nostatus=nostatus,
            path=path,
            bounds=bounds,
            count=count,
            continue_cursor=continue_cursor,
            sort=sort,
            descending=descending,
            fields=fields,
            include_content=include_content,
            include_links=include_links,
            created_from=created_from,
            created_to=created_to,
            updated_from=updated_from,
            updated_to=updated_to,
            severity=severity,
            severity_min=severity_min,
            severity_max=severity_max,
            no_severity=no_severity,
        )
