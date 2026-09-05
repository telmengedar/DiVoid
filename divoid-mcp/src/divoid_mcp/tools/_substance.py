"""
Shared substance write for the composite create tools.

Sets Node.Substance on a fully created node by delegating to divoid_patch_node's
replace /substance op.
"""

from __future__ import annotations

from typing import Any

from ..config import DivoidConfig
from ..errors import make_error_content
from .patch_node import _execute as _patch_node_execute


async def write_substance(
    node_id: int,
    substance: str | None,
    config: "DivoidConfig",
) -> dict[str, Any] | None:
    """
    Write substance onto a created node, or do nothing when the caller supplied none.

    Runs as the last step of a composite create, so a failure leaves the node
    complete apart from substance. Returns a partial_state error envelope when the
    write fails, None on success and when there is nothing to write.
    """
    if substance is None:
        return None

    result = await _patch_node_execute(id=node_id, config=config, substance=substance)
    if not result.get("isError"):
        return None

    detail = result["content"][0]["text"]
    return {
        "isError": True,
        "content": make_error_content(
            "partial_state",
            f"Node #{node_id} was created, its content posted and its links made, but "
            f"the substance write failed: {detail}. The node is complete apart from "
            f"substance; repair manually (PATCH /api/nodes/{node_id} replace "
            f"/substance) or delete it.",
        ),
    }
