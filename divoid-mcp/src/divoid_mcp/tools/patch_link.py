"""
divoid_patch_link -- primitive JSON-Patch wrapper around
PATCH /api/nodes/{source_id}/links/{target_id}.

Edits an existing edge's LinkType/Context in place (backend DiVoid #7201 / PR
#170), closing the rich-NodeLink arc on the MCP side. Mirrors
divoid_patch_node's shape: friendly kwargs, compose the JSON-Patch array
internally, invariant guard before any HTTP call, a `clear_*` boolean for the
nullable field.

Supported paths (backend [AllowPatch] on NodeLink, per PR #170):
  /linkType -- string enum name ("None"/"Unidirectional"/"Bidirectional").
               The backend is the authority on valid values -- no client-side
               allow-list (divoid-mcp CLAUDE.md invariant 6). There is no
               clear_link_type: LinkType is non-nullable on the server
               (defaults to "None"); pass link_type="None" to make an edge
               undirected again.
  /context  -- free-text label, read source_id -> target_id. Use
               clear_context=True to set it to NULL.

The edge is addressed the same way divoid_unlink_nodes addresses it: either
stored orientation of (source_id, target_id) matches -- the backend reloads
and returns the edge in its true stored orientation, so the caller can read
back which end is actually source.

Unlike divoid_unlink_nodes, a missing edge is a hard 404 (node_not_found via
the standard error mapper), not an idempotent no-op -- there is nothing
sensible to patch on an edge that does not exist.

At least one of link_type/context/clear_context must be provided -- the
invariant guard fires before any HTTP call with code 'no_fields_to_patch'.

Architecture reference: DiVoid #7206 (this tool). Backend reference: DiVoid
#7201 / PR #170 (PATCH /api/nodes/{source}/links/{target}).
"""

from __future__ import annotations

import logging
from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import InvariantViolation, make_error_content, map_http_error, map_unreachable
from ._link_details import normalize_link_detail

logger = logging.getLogger(__name__)

_TOOL_DESCRIPTION = """\
Edit an existing link's linkType and/or context in place. Wraps \
PATCH /api/nodes/{source_id}/links/{target_id} (backend DiVoid #7201 / PR #170). \
The edge is addressed the same way divoid_unlink_nodes addresses it -- either \
stored orientation of (source_id, target_id) matches. link_type accepts the \
backend's LinkType enum string name (e.g. "None", "Unidirectional", \
"Bidirectional") -- no client-side allow-list (invariant 6); pass \
link_type="None" to make an edge undirected again (there is no \
clear_link_type -- LinkType is non-nullable on the server). context is a \
free-text label read source_id -> target_id; use clear_context=True to set it \
to NULL. At least one of link_type, context, or clear_context must be provided \
(invariant guard: no_fields_to_patch). A missing edge is a hard 404 \
(node_not_found) -- not a silent no-op, unlike divoid_unlink_nodes. Returns the \
patched edge normalized to {source_id, target_id, link_type, context}, in its \
true stored orientation.\
"""


def _check_invariants(
    link_type: str | None,
    context: str | None,
    clear_context: bool,
) -> None:
    """
    Check the no-fields-to-patch invariant before making any HTTP call.

    Raises InvariantViolation with a stable code if no field is provided.
    Mirrors divoid_patch_node's _check_invariants shape. The structural
    source_id/target_id guards live inline in register() (mirroring
    divoid_link_nodes/divoid_unlink_nodes), not here.
    """
    if link_type is None and context is None and not clear_context:
        raise InvariantViolation(
            "no_fields_to_patch",
            "At least one of link_type, context, or clear_context must be "
            "provided. A PATCH with no fields is a no-op.",
        )


async def _execute(
    source_id: int,
    target_id: int,
    config: "DivoidConfig",
    link_type: str | None = None,
    context: str | None = None,
    clear_context: bool = False,
) -> dict[str, Any]:
    """
    Core implementation of divoid_patch_link.

    Extracted from register() so smoke tests can call it directly -- if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.

    Callers must run _check_invariants() before calling this function.
    """
    ops: list[dict[str, Any]] = []
    if link_type is not None:
        ops.append({"op": "replace", "path": "/linkType", "value": link_type})
    if context is not None:
        ops.append({"op": "replace", "path": "/context", "value": context})
    elif clear_context:
        ops.append({"op": "replace", "path": "/context", "value": None})

    logger.info(
        "divoid_patch_link source=%d target=%d ops=%s",
        source_id, target_id, [op["path"] for op in ops],
    )

    try:
        result = await http_client.patch_json(f"nodes/{source_id}/links/{target_id}", ops)
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, "divoid_patch_link")
        logger.warning("divoid_patch_link err=%s", code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not result.ok:
        code, msg = map_http_error(
            result.status, result.body, config.api_key, "divoid_patch_link"
        )
        logger.info(
            "divoid_patch_link source=%d target=%d err=%s status=%d",
            source_id, target_id, code, result.status,
        )
        return {"isError": True, "content": make_error_content(code, msg)}

    try:
        link_data = result.json()
    except Exception as exc:
        return {
            "isError": True,
            "content": make_error_content(
                "divoid_bad_request",
                f"divoid_patch_link: Could not parse response: {exc}",
            ),
        }

    logger.info("divoid_patch_link source=%d target=%d ok", source_id, target_id)
    return normalize_link_detail(link_data)


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_patch_link(
        source_id: int,
        target_id: int,
        link_type: str | None = None,
        context: str | None = None,
        clear_context: bool = False,
    ) -> dict[str, Any]:
        """
        Edit an existing link's linkType and/or context in place.

        The edge is addressed the same way divoid_unlink_nodes addresses it --
        either stored orientation of (source_id, target_id) matches.

        Args:
            source_id: One of the two nodes the edge connects. Must be a
                       positive integer.
            target_id: The other node the edge connects. Must be a positive
                       integer and different from source_id.
            link_type: New direction semantics for the edge -- the backend's
                       LinkType enum string name (e.g. "None",
                       "Unidirectional", "Bidirectional"). The backend is the
                       authority on valid values; this tool does not enforce
                       a client-side allow-list. There is no clear_link_type
                       -- LinkType is non-nullable on the server; pass
                       link_type="None" to make an edge undirected again.
            context: New free-text label carried on the edge, read
                     source_id -> target_id. To clear an existing context
                     (set it to NULL), pass clear_context=True instead.
            clear_context: If True, sets context to NULL on the server.
                           Mutually implied exclusive with context -- if both
                           are set, the explicit context value wins.

        At least one of link_type, context, or clear_context must be provided
        (invariant guard: no_fields_to_patch). A missing edge between
        source_id and target_id returns a hard 404 (node_not_found) --
        unlike divoid_unlink_nodes, this is not treated as an idempotent
        no-op.
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

        try:
            _check_invariants(link_type, context, clear_context)
        except InvariantViolation as exc:
            logger.debug("divoid_patch_link invariant violation: %s", exc.code)
            return {"isError": True, "content": make_error_content(exc.code, exc.message)}

        return await _execute(
            source_id=source_id,
            target_id=target_id,
            config=config,
            link_type=link_type,
            context=context,
            clear_context=clear_context,
        )
