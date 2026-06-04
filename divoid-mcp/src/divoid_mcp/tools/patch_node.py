"""
divoid_patch_node -- primitive JSON-Patch wrapper around PATCH /api/nodes/{id}.

Accepts the patchable properties (name, status, x, y, access, owner_id) as
explicit parameters and composes the JSON-Patch array internally. The caller
does not need to know the patch format.

Supported paths per DiVoid #8:
  /name     -- node name (string)
  /status   -- node status (string)
  /X        -- canvas X position (number)
  /Y        -- canvas Y position (number)
  /access   -- visibility flags: None=0, Read=1, Write=2, Read|Write=3 (default)
               Accepts int 0-3 or string "None"/"Read"/"Write"/"Read, Write".
               Canonicalized to int before composing the patch op.
               Owner-or-admin gated on the server.
  /ownerId  -- transfer ownership (admin-only on the server; 404 if unauthorized).

At least one of the six must be provided — the invariant guard fires before
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
Primitive JSON-Patch update for a DiVoid node. Accepts name, status, severity, x, y, \
access, and owner_id as explicit parameters and composes the PATCH /api/nodes/{id} call \
internally. At least one field must be provided (invariant guard: no_fields_to_patch). \
Returns the updated node on success. For status changes that should enforce the \
type's lifecycle (task/bug), use divoid_set_status instead — this tool accepts \
any string for status without validation. access accepts int 0-3 or the string \
representation ("None", "Read", "Write", "Read, Write") and is canonicalized to int \
before patching. owner_id transfer is admin-only on the server (returns 404 if \
unauthorized — the MCP does not gate it). severity accepts a positive integer or null; \
use replace null as the canonical clear verb (sets severity to NULL on the server).\
"""

_ACCESS_STRING_MAP: dict[str, int] = {
    "None": 0,
    "Read": 1,
    "Write": 2,
    "Read, Write": 3,
}


def _canonicalize_access(access: int | str) -> int:
    if isinstance(access, int):
        return access
    mapped = _ACCESS_STRING_MAP.get(access)
    if mapped is None:
        raise InvariantViolation(
            "access_invalid_value",
            f"access string {access!r} is not recognized. "
            f"Valid strings: {', '.join(repr(k) for k in _ACCESS_STRING_MAP)}. "
            "Or pass an integer 0-3 directly.",
        )
    return mapped


def _check_invariants(
    name: str | None,
    status: str | None,
    x: float | None,
    y: float | None,
    access: int | str | None,
    owner_id: int | None,
    severity: int | None = None,
    clear_severity: bool = False,
) -> None:
    """
    Check runtime invariants before making any HTTP call.

    Raises InvariantViolation with a stable code if any invariant is broken.
    The invariant guard is the sole enforcement layer — FastMCP exposes
    parameters as plain {"type": "string"} / {"type": "number"} without
    cross-parameter constraints; enforcement is entirely here.
    """
    has_severity_op = severity is not None or clear_severity
    if (name is None and status is None and x is None and y is None
            and access is None and owner_id is None and not has_severity_op):
        raise InvariantViolation(
            "no_fields_to_patch",
            "At least one of name, status, severity, x, y, access, or owner_id must be provided. "
            "A PATCH with no fields is a no-op.",
        )
    if access is not None:
        _canonicalize_access(access)


async def _execute(
    id: int,
    config: "DivoidConfig",
    name: str | None = None,
    status: str | None = None,
    x: float | None = None,
    y: float | None = None,
    access: int | str | None = None,
    owner_id: int | None = None,
    severity: int | None = None,
    clear_severity: bool = False,
) -> dict[str, Any]:
    """
    Core implementation of divoid_patch_node.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.

    Callers must run _check_invariants() before calling this function.
    """
    ops: list[dict[str, Any]] = []
    if name is not None:
        ops.append({"op": "replace", "path": "/name", "value": name})
    if status is not None:
        ops.append({"op": "replace", "path": "/status", "value": status})
    if x is not None:
        ops.append({"op": "replace", "path": "/X", "value": x})
    if y is not None:
        ops.append({"op": "replace", "path": "/Y", "value": y})
    if access is not None:
        ops.append({"op": "replace", "path": "/access", "value": _canonicalize_access(access)})
    if owner_id is not None:
        ops.append({"op": "replace", "path": "/ownerId", "value": owner_id})
    if severity is not None:
        ops.append({"op": "replace", "path": "/severity", "value": severity})
    elif clear_severity:
        ops.append({"op": "replace", "path": "/severity", "value": None})

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
        access: int | str | None = None,
        owner_id: int | None = None,
        severity: int | None = None,
        clear_severity: bool = False,
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
            access: Visibility flags. Accepts int (0-3) or string ("None", "Read",
                    "Write", "Read, Write"). Canonicalized to int before composing
                    the patch op. None=0, Read=1, Write=2, Read|Write=3 (default).
                    Owner-or-admin gated on the server.
            owner_id: Transfer ownership to this user id. Admin-only on the server —
                      returns 404 if the caller lacks permission.
            severity: New severity value (positive integer). Optional. To clear
                      severity (set to NULL), pass clear_severity=True instead —
                      that is the canonical clear verb per DiVoid #1609 design §13.
            clear_severity: If True, sets severity to NULL on the server. Mutually
                            implied exclusive with severity — if both are set, the
                            explicit severity value wins.

        At least one of name, status, severity, clear_severity, x, y, access, or
        owner_id must be provided (invariant guard: no_fields_to_patch).
        """
        try:
            _check_invariants(name, status, x, y, access, owner_id, severity, clear_severity)
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
            access=access,
            owner_id=owner_id,
            severity=severity,
            clear_severity=clear_severity,
        )
