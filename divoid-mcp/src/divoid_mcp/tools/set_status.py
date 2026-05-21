"""
divoid_set_status -- typed status-update wrapper with lifecycle validation.

Convenience tool over divoid_patch_node that ALSO validates the new status
against the node type's lifecycle per DiVoid #493 §5 before making any HTTP call.

Lifecycle rules:
  type=task:  status must be in {new, open, in-progress, closed}
              → violation code: status_not_in_task_lifecycle
  type=bug:   status must be in {new, open, in-progress, fixed}
              → violation code: status_not_in_bug_lifecycle
  all others: status is not supported at all (per #493 §5, only task and bug
              have a lifecycle; documentation, session-log, etc. reject status)
              → violation code: status_not_supported_for_type

The tool fetches the node type via GET /api/nodes/{id} before patching, adding
one round-trip. That round-trip is intentional: the type-check is the main value
of this tool over a raw divoid_patch_node call.

Architecture reference: DiVoid #695 §Tool 2 (set_status typed wrapper).
API reference: DiVoid #8 (PATCH /api/nodes/{id}, GET /api/nodes/{id}).
Structural conventions: DiVoid #493 §5 (status lifecycle).
"""

from __future__ import annotations

import logging
from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import InvariantViolation, make_error_content, map_http_error, map_unreachable

logger = logging.getLogger(__name__)

# Lifecycle maps per DiVoid #493 §5.
_TASK_STATUSES = frozenset({"new", "open", "in-progress", "closed"})
_BUG_STATUSES = frozenset({"new", "open", "in-progress", "fixed"})
# Types that carry a status lifecycle. All others reject status entirely.
_LIFECYCLE_TYPES = frozenset({"task", "bug"})

_TOOL_DESCRIPTION = """\
Set the status of a DiVoid node with lifecycle validation. Fetches the node's \
type first, then validates the requested status against that type's lifecycle \
(per DiVoid #493 §5) before patching. Use this instead of divoid_patch_node when \
you need the type check: task accepts new/open/in-progress/closed; bug accepts \
new/open/in-progress/fixed; all other types reject status entirely. Violations \
are caught before any write — the invariant guard fires with a stable code naming \
the violated rule.\
"""


def _validate_status_for_type(node_type: str, status: str) -> None:
    """
    Validate the status against the node type's lifecycle.

    Raises InvariantViolation with a stable code if the status is not valid
    for the given type. The invariant guard is the sole enforcement layer —
    FastMCP exposes parameters as plain {"type": "string"}; enforcement is
    entirely here.

    Per DiVoid #493 §5:
      - task: {new, open, in-progress, closed}
      - bug:  {new, open, in-progress, fixed}
      - all others: no status lifecycle (status is rejected entirely)
    """
    if node_type == "task":
        if status not in _TASK_STATUSES:
            raise InvariantViolation(
                "status_not_in_task_lifecycle",
                f"Status '{status}' is not valid for type 'task'. "
                f"Allowed: {', '.join(sorted(_TASK_STATUSES))}. "
                "See DiVoid #493 §5 for the task lifecycle.",
            )
    elif node_type == "bug":
        if status not in _BUG_STATUSES:
            raise InvariantViolation(
                "status_not_in_bug_lifecycle",
                f"Status '{status}' is not valid for type 'bug'. "
                f"Allowed: {', '.join(sorted(_BUG_STATUSES))}. "
                "See DiVoid #493 §5 for the bug lifecycle.",
            )
    else:
        raise InvariantViolation(
            "status_not_supported_for_type",
            f"Type '{node_type}' has no status lifecycle (per DiVoid #493 §5). "
            "Only 'task' and 'bug' nodes carry a status. "
            "Do not set status on documentation, session-log, or other structural nodes.",
        )


async def _execute(
    id: int,
    status: str,
    config: "DivoidConfig",
) -> dict[str, Any]:
    """
    Core implementation of divoid_set_status.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.

    Steps:
      1. GET /nodes/{id} to fetch the node's current type.
      2. Validate status against type lifecycle (_validate_status_for_type).
      3. PATCH /nodes/{id} with [{"op": "replace", "path": "/status", "value": status}].
      4. Return the updated node.
    """
    logger.info("divoid_set_status id=%d status=%r", id, status)

    # --- Step 1: Fetch node type ---
    try:
        node_result = await http_client.get(f"nodes/{id}")
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, f"fetch node #{id} for type check")
        logger.warning("divoid_set_status id=%d step=fetch_node err=%s", id, code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not node_result.ok:
        code, msg = map_http_error(
            node_result.status, node_result.body, config.api_key,
            f"fetch node #{id} for type check",
        )
        logger.info("divoid_set_status id=%d step=fetch_node err=%s status=%d", id, code, node_result.status)
        return {"isError": True, "content": make_error_content(code, msg)}

    try:
        node_data = node_result.json()
        node_type: str = node_data.get("type", "")
    except Exception as exc:
        return {
            "isError": True,
            "content": make_error_content(
                "divoid_bad_request",
                f"fetch node #{id}: Could not parse response: {exc}",
            ),
        }

    if not node_type:
        return {
            "isError": True,
            "content": make_error_content(
                "node_type_unknown",
                f"Node #{id} has no type — cannot validate status lifecycle. "
                "Use divoid_patch_node to set status without lifecycle checks.",
            ),
        }

    # --- Step 2: Validate status against lifecycle (invariant guard) ---
    try:
        _validate_status_for_type(node_type, status)
    except InvariantViolation as exc:
        logger.debug("divoid_set_status id=%d invariant violation: %s (type=%s)", id, exc.code, node_type)
        return {"isError": True, "content": make_error_content(exc.code, exc.message)}

    # --- Step 3: PATCH the status ---
    ops = [{"op": "replace", "path": "/status", "value": status}]
    try:
        patch_result = await http_client.patch_json(f"nodes/{id}", ops)
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, f"PATCH status on node #{id}")
        logger.warning("divoid_set_status id=%d step=patch err=%s", id, code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not patch_result.ok:
        code, msg = map_http_error(
            patch_result.status, patch_result.body, config.api_key,
            f"PATCH status on node #{id}",
        )
        logger.info("divoid_set_status id=%d step=patch err=%s status=%d", id, code, patch_result.status)
        return {"isError": True, "content": make_error_content(code, msg)}

    # Return the updated node.
    try:
        updated_node = patch_result.json()
    except Exception as exc:
        return {
            "isError": True,
            "content": make_error_content(
                "divoid_bad_request",
                f"PATCH status node #{id}: Could not parse response: {exc}",
            ),
        }

    logger.info("divoid_set_status id=%d ok new_status=%r", id, status)
    return updated_node


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_set_status(
        id: int,
        status: str,
    ) -> dict[str, Any]:
        """
        Set the status of a DiVoid node, validated against its type's lifecycle.

        Args:
            id: The node id to update (required).
            status: The new status value (required). Valid values depend on
                    the node type (per DiVoid #493 §5):
                      task: new, open, in-progress, closed
                      bug:  new, open, in-progress, fixed
                      all others: rejected — types like documentation, session-log
                                  have no lifecycle; calling this on them fires the
                                  status_not_supported_for_type invariant guard.
                    FastMCP exposes status as a plain string parameter — validation
                    is entirely in the runtime invariant guard, not in JSON Schema.
        """
        return await _execute(id=id, status=status, config=config)
