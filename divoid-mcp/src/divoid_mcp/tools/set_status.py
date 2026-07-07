"""
divoid_set_status -- free-form status setter for any DiVoid node.

Ergonomic single-parameter wrapper over divoid_patch_node that sets the status
of any node to whatever string the caller provides. The backend (DiVoid #24) is
the authority on what status values are meaningful — the vocabulary is documented
in the graph as `status`-typed nodes, but is not enforced in code.

Any free-form status string is accepted: `open`, `closed`, `norepro`, `wontfix`,
`draft`, `superseded` — anything the graph has agreed on. Adding a new status
costs zero code: create a `status` node in the graph and start using the value.

API reference: DiVoid #8 (PATCH /api/nodes/{id}).
Status model design: DiVoid #24 (Node lifecycle / status model).
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
Set the status of any DiVoid node to any free-form string. Accepts any status \
value on any node type — the vocabulary (open, closed, norepro, fixed, draft, \
etc.) is defined by graph convention (DiVoid #24), not enforced here. Use this \
instead of divoid_patch_node when you only need to update status; the signature \
is simpler (id + status) and the intent is clearer in tool call logs.\
"""


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

    Issues PATCH /nodes/{id} with [{"op": "replace", "path": "/status", "value": status}]
    and returns the updated node. No type fetch, no allow-list check — the backend
    is the authority on what is valid.
    """
    logger.info("divoid_set_status id=%d status=%r", id, status)

    ops = [{"op": "replace", "path": "/status", "value": status}]
    try:
        patch_result = await http_client.patch_json(f"nodes/{id}", ops)
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, f"PATCH status on node #{id}")
        logger.warning("divoid_set_status id=%d err=%s", id, code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not patch_result.ok:
        code, msg = map_http_error(
            patch_result.status, patch_result.body, config.api_key,
            f"PATCH status on node #{id}",
        )
        logger.info("divoid_set_status id=%d err=%s http_status=%d", id, code, patch_result.status)
        return {"isError": True, "content": make_error_content(code, msg)}

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
        Set the status of a DiVoid node to any free-form string.

        Args:
            id: The node id to update (required).
            status: The new status value (required). Any string is accepted —
                    the vocabulary is a graph convention (DiVoid #24), not enforced
                    here. Examples: open, closed, in-progress, norepro, fixed,
                    wontfix, draft, superseded. The backend is the authority.
        """
        return await _execute(id=id, status=status, config=config)
