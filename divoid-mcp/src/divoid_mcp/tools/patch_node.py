"""
divoid_patch_node -- primitive JSON-Patch wrapper around PATCH /api/nodes/{id}.

Accepts the patchable properties (name, status, x, y) as explicit parameters
and composes the JSON-Patch array internally. The caller does not need to know
the patch format.

Supported paths per DiVoid #8:
  /name   -- node name (string)
  /status -- node status (string)
  /X      -- canvas X position (number)
  /Y      -- canvas Y position (number)

At least one of the four must be provided — the invariant guard fires before
any HTTP call with code 'no_fields_to_patch'.

Note: for status changes with lifecycle validation, use divoid_set_status
instead. divoid_patch_node accepts any string for status without checking the
type's lifecycle.

Architecture reference: DiVoid #695 §Tool 1 (patch_node primitive).
API reference: DiVoid #8 (PATCH /api/nodes/{id}).
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
Primitive JSON-Patch update for a DiVoid node. Accepts name, status, x, and y \
as explicit parameters and composes the PATCH /api/nodes/{id} call internally. \
At least one of the four fields must be provided (invariant guard: no_fields_to_patch). \
Returns the updated node on success. For status changes that should enforce the \
type's lifecycle (task/bug), use divoid_set_status instead — this tool accepts \
any string for status without validation.\
"""


def _check_invariants(
    name: str | None,
    status: str | None,
    x: float | None,
    y: float | None,
) -> None:
    """
    Check runtime invariants before making any HTTP call.

    Raises InvariantViolation with a stable code if any invariant is broken.
    The invariant guard is the sole enforcement layer — FastMCP exposes
    parameters as plain {"type": "string"} / {"type": "number"} without
    cross-parameter constraints; enforcement is entirely here.
    """
    if name is None and status is None and x is None and y is None:
        raise InvariantViolation(
            "no_fields_to_patch",
            "At least one of name, status, x, or y must be provided. "
            "A PATCH with no fields is a no-op.",
        )


async def _execute(
    id: int,
    config: "DivoidConfig",
    name: str | None = None,
    status: str | None = None,
    x: float | None = None,
    y: float | None = None,
) -> dict[str, Any]:
    """
    Core implementation of divoid_patch_node.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.

    Callers must run _check_invariants() before calling this function.
    """
    # Build the JSON-Patch array from provided fields.
    ops: list[dict[str, Any]] = []
    if name is not None:
        ops.append({"op": "replace", "path": "/name", "value": name})
    if status is not None:
        ops.append({"op": "replace", "path": "/status", "value": status})
    if x is not None:
        ops.append({"op": "replace", "path": "/X", "value": x})
    if y is not None:
        ops.append({"op": "replace", "path": "/Y", "value": y})

    logger.info(
        "divoid_patch_node id=%d ops=%s",
        id, [op["path"] for op in ops],
    )

    try:
        result = await http_client.patch_json(f"nodes/{id}", ops)
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, f"PATCH node #{id}")
        logger.warning("divoid_patch_node id=%d err=%s", id, code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not result.ok:
        code, msg = map_http_error(result.status, result.body, config.api_key, f"PATCH node #{id}")
        logger.info("divoid_patch_node id=%d err=%s status=%d", id, code, result.status)
        return {"isError": True, "content": make_error_content(code, msg)}

    # The PATCH endpoint returns the updated node.
    try:
        node_data = result.json()
    except Exception as exc:
        return {
            "isError": True,
            "content": make_error_content(
                "divoid_bad_request",
                f"PATCH node #{id}: Could not parse response: {exc}",
            ),
        }

    logger.info("divoid_patch_node id=%d ok", id)
    return node_data


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_patch_node(
        id: int,
        name: str | None = None,
        status: str | None = None,
        x: float | None = None,
        y: float | None = None,
    ) -> dict[str, Any]:
        """
        Patch one or more properties of a DiVoid node.

        Args:
            id: The node id to patch (required).
            name: New name for the node. Optional.
            status: New status string. Optional. No lifecycle validation is
                    performed here — use divoid_set_status if you want the type's
                    lifecycle enforced (task: new/open/in-progress/closed;
                    bug: new/open/in-progress/fixed). The invariant guard is the
                    sole enforcement layer (FastMCP exposes status as plain string).
            x: New canvas X position (world units). Optional.
            y: New canvas Y position (world units). Optional.

        At least one of name, status, x, or y must be provided (invariant guard:
        no_fields_to_patch — FastMCP does not enforce cross-parameter requirements).
        """
        # --- Invariant guard (before any HTTP call) ---
        try:
            _check_invariants(name, status, x, y)
        except InvariantViolation as exc:
            logger.debug("divoid_patch_node invariant violation: %s", exc.code)
            return {"isError": True, "content": make_error_content(exc.code, exc.message)}

        return await _execute(
            id=id,
            config=config,
            name=name,
            status=status,
            x=x,
            y=y,
        )
