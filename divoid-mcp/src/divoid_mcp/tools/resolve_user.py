"""
divoid_resolve_user -- primitive: resolve an agent/person node-id to its user-id.

Thin wrapper around GET /api/nodes/{id}/user (live since PR #73, 2026-05-18).

Use this when you have a node-id for an agent or person and need their user-id
for messaging or any other user-scoped operation. The canonical routing flow in
the Hivemind Protocol (DiVoid #435) is:

  1. Walk the graph to find the agent/person node (type=agent or type=person).
  2. Call divoid_resolve_user with that node_id.
  3. Use the returned user_id as recipientId in divoid_send_message.

Routing pattern (per #435): find the human responsible for the work, then find
the notifier agent linked to that human (type=agent, linked to the person node),
then call this tool with the agent's node-id. The returned user-id is the
messaging recipient. A human's own user-id is never the recipient — humans do
not poll the inbox.

The 404 response is collapsed by the API: it means either the node does not exist
OR the node exists but has no User record linked to it via User.HomeNodeId. If you
need to distinguish, call divoid_get_node first — 200 there + 404 here means
"node exists, no user binding yet." Surface that as a binding-gap rather than
falling back to addressing a human's user-id or guessing.
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
Resolve an agent or person node-id to its DiVoid user-id via \
GET /api/nodes/{id}/user. \
Returns {user_id: long} on success. \
Use this to obtain the user-id needed by divoid_send_message or other \
user-scoped operations. Typical routing per DiVoid #435: find the human \
responsible for the topic, find the notifier agent linked to that human, \
resolve THAT agent's node-id here, then send to the resolved user-id. A \
human's own user-id is never the right messaging recipient — humans do not \
poll the inbox. \
A 404 means either the node doesn't exist or it has no User binding yet (the API \
collapses both); surface that as a binding-gap rather than falling back to a guess.\
"""


async def _execute(node_id: int, config: "DivoidConfig") -> dict[str, Any]:
    """
    Core implementation of divoid_resolve_user.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.
    """
    logger.info("divoid_resolve_user node_id=%d", node_id)

    try:
        result = await http_client.get(f"nodes/{node_id}/user")
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, f"resolve user for node #{node_id}")
        logger.warning("divoid_resolve_user node_id=%d err=%s", node_id, code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not result.ok:
        code, msg = map_http_error(
            result.status, result.body, config.api_key, f"resolve user for node #{node_id}"
        )
        logger.info(
            "divoid_resolve_user node_id=%d err=%s status=%d",
            node_id, code, result.status,
        )
        # Surface the original 404 envelope without over-disambiguation — the API
        # intentionally collapses "no such node" and "node has no User binding".
        return {"isError": True, "content": make_error_content(code, msg)}

    try:
        data = result.json()
        user_id: int = data["userId"]
    except Exception as exc:
        return {
            "isError": True,
            "content": make_error_content(
                "divoid_bad_request",
                f"resolve user for node #{node_id}: Could not parse response: {exc}",
            ),
        }

    logger.info("divoid_resolve_user node_id=%d -> user_id=%d", node_id, user_id)
    return {"user_id": user_id}


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_resolve_user(node_id: int) -> dict[str, Any]:
        """
        Resolve an agent/person node-id to its DiVoid user-id.

        Args:
            node_id: The graph node id of the agent or person whose user-id you need.
                     Enforcement: node_id must be a positive integer; FastMCP exposes
                     it as plain {"type": "integer"} in the JSON Schema — the invariant
                     guard (positive-int check) is the sole enforcement layer.
        """
        if not isinstance(node_id, int) or node_id <= 0:
            return {
                "isError": True,
                "content": make_error_content(
                    "invalid_node_id",
                    f"node_id must be a positive integer, got {node_id!r}.",
                ),
            }
        return await _execute(node_id=node_id, config=config)
