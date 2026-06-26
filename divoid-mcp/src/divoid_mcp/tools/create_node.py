"""
divoid_create_node -- generic composite tool: create a node of any type.

The type-specific creators (create_task, create_documentation, create_session_log)
enforce per-type invariants (content-required, group auto-resolution, lifecycle
status values) and are the preferred path for their covered types. This tool is
the low-level escape hatch for everything they do not cover: meeting, plan,
project, group/untyped nodes (type=None), event, chat, and any future types.

From the caller's perspective this is a single atomic operation. Under the hood
it makes up to N+2 HTTP calls:
  1. POST /nodes                          -- create the node
  2. POST /nodes/{id}/content             -- set the content body (if provided)
  3. POST /nodes/{id}/links  ×N           -- link to each id in extra_links

Partial failure semantics (per architecture §6.3): if step 1 succeeds but step 2
or 3 fails, the server does NOT roll back. The tool returns an MCP error naming
the surviving node id and the missing step so the caller can repair manually.

Invariant guard (before any HTTP call):
  - name must be non-empty (the only hard guard; everything else is optional)
  - access, if provided, must be a valid int or recognised string

No group auto-resolution, no content-required check, no lifecycle enforcement.
Those deliberate conveniences belong to the type-specific tools.

Architecture reference: §8.4, §6.3. DiVoid task #1364.
"""

from __future__ import annotations

import logging
from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import InvariantViolation, make_error_content, map_http_error, map_unreachable
from .patch_node import _canonicalize_access

logger = logging.getLogger(__name__)

_TOOL_DESCRIPTION = """\
Generic atomic create for any DiVoid node type. Use this when the type-specific \
creators (divoid_create_task, divoid_create_documentation, divoid_create_session_log) \
don't cover the type you need — meeting, plan, project, group (type=None/omitted), \
event, chat, or any custom type. Creates the node, optionally sets its content \
(UTF-8 safe), and optionally links it to one or more existing nodes — all in one call. \
No content-required check, no group auto-resolution, no lifecycle status validation; \
those invariants belong to the type-specific tools. The only hard requirement is a \
non-empty name. On partial failure (node created but content or link step fails) the \
tool returns isError with code=partial_state naming the surviving node id so the \
caller can repair manually.\
"""


def _check_invariants(name: str, access: int | str | None) -> None:
    """
    Check runtime invariants before making any HTTP call.

    Raises InvariantViolation with a stable code if any invariant is broken.
    This is the sole enforcement layer — FastMCP exposes parameters as plain
    JSON Schema types without cross-parameter constraints.
    """
    if not name or not name.strip():
        raise InvariantViolation(
            "name_required",
            "name must be a non-empty string. "
            "Lead with a search-friendly title (e.g. '2026-06-26 — Tech Sync: ...').",
        )
    if access is not None:
        _canonicalize_access(access)


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_create_node(
        name: str,
        type: str | None = None,
        content: str | None = None,
        status: str | None = None,
        severity: int | None = None,
        access: int | str | None = None,
        extra_links: list[int] | None = None,
    ) -> dict[str, Any]:
        """
        Create a DiVoid node of any type atomically.

        Args:
            name: Search-friendly title (required, non-empty). Lead with an area
                  prefix or date when scope is obvious (e.g. '2026-06-26 — Tech Sync').
            type: Node type string. Use any recognised DiVoid type ('meeting', 'plan',
                  'project', 'event', 'chat', 'documentation', 'task', etc.) or omit /
                  pass None to create an untyped group/container node.
            content: Optional body text. Posted as UTF-8 bytes (no shell encoding trap).
                     If omitted the node is created content-empty, which is valid for
                     group nodes and quick captures. For types where content is
                     structurally required (task, documentation, session-log) prefer the
                     type-specific creator tools which enforce the content invariant.
            status: Optional status string, passed through verbatim. No lifecycle
                    validation is applied — any string is accepted. Use the type-specific
                    creators or divoid_set_status if you want lifecycle enforcement.
            severity: Optional integer severity. Application scope fills in meaning
                      (e.g. priority). When absent the server defaults to NULL.
            access: Visibility flags. Accepts int (0-3) or string ("None", "Read",
                    "Write", "Read, Write"). Canonicalized to int before the POST.
                    When absent the server defaults to Read|Write (3).
            extra_links: Optional list of node ids to link the new node to atomically.
                         Each id receives one POST /nodes/{id}/links call. No dedup is
                         applied — the server treats duplicate links as idempotent.
        """
        if extra_links is None:
            extra_links = []

        try:
            _check_invariants(name, access)
        except InvariantViolation as exc:
            logger.debug("divoid_create_node invariant violation: %s", exc.code)
            return {"isError": True, "content": make_error_content(exc.code, exc.message)}

        logger.info(
            "divoid_create_node name=%r type=%s status=%s extra_links=%s",
            name[:60], type, status, extra_links,
        )

        node_body: dict[str, Any] = {"name": name}
        if type is not None and type.strip():
            node_body["type"] = type
        if status is not None:
            node_body["status"] = status
        if severity is not None:
            node_body["severity"] = severity
        if access is not None:
            try:
                node_body["access"] = _canonicalize_access(access)
            except InvariantViolation as exc:
                logger.debug("divoid_create_node invariant violation: %s", exc.code)
                return {"isError": True, "content": make_error_content(exc.code, exc.message)}

        try:
            create_result = await http_client.post_json("nodes", node_body)
        except http_client.DiVoidUnreachable as exc:
            code, msg = map_unreachable(exc, config.api_key, "create node")
            logger.warning("divoid_create_node step=create_node err=%s", code)
            return {"isError": True, "content": make_error_content(code, msg)}

        if not create_result.ok:
            code, msg = map_http_error(create_result.status, create_result.body, config.api_key, "create node")
            logger.info("divoid_create_node step=create_node err=%s status=%d", code, create_result.status)
            return {"isError": True, "content": make_error_content(code, msg)}

        try:
            node_data = create_result.json()
            node_id: int = node_data["id"]
        except Exception as exc:
            return {
                "isError": True,
                "content": make_error_content(
                    "divoid_bad_request",
                    f"create node: Could not parse response: {exc}",
                ),
            }

        logger.info("divoid_create_node node_id=%d created type=%s", node_id, type)

        content_posted = False
        content_length = 0
        if content and content.strip():
            content_bytes = content.encode("utf-8")
            try:
                content_result = await http_client.post_bytes(
                    f"nodes/{node_id}/content",
                    content_bytes,
                    "text/markdown; charset=utf-8",
                )
            except http_client.DiVoidUnreachable as exc:
                code, msg = map_unreachable(exc, config.api_key, f"post content for node #{node_id}")
                logger.warning("divoid_create_node node_id=%d step=post_content err=%s", node_id, code)
                return {
                    "isError": True,
                    "content": make_error_content(
                        "partial_state",
                        f"Node #{node_id} was created successfully but content POST failed: "
                        f"{code}: {msg}. The node exists and is content-empty; repair manually "
                        f"(POST /api/nodes/{node_id}/content) or delete it.",
                    ),
                }

            if not content_result.ok:
                code, msg = map_http_error(
                    content_result.status, content_result.body, config.api_key,
                    f"post content for node #{node_id}",
                )
                logger.warning(
                    "divoid_create_node node_id=%d step=post_content err=%s status=%d",
                    node_id, code, content_result.status,
                )
                return {
                    "isError": True,
                    "content": make_error_content(
                        "partial_state",
                        f"Node #{node_id} was created successfully but content POST failed "
                        f"({content_result.status}): {msg}. The node exists but has no content; "
                        f"repair manually (POST /api/nodes/{node_id}/content) or delete it.",
                    ),
                }

            content_posted = True
            content_length = len(content_bytes)
            logger.info("divoid_create_node node_id=%d content_posted byte_length=%d", node_id, content_length)

        links_created: list[int] = []
        for link_target in extra_links:
            try:
                link_result = await http_client.post_json(f"nodes/{node_id}/links", link_target)
            except http_client.DiVoidUnreachable as exc:
                code, msg = map_unreachable(exc, config.api_key, f"link node #{node_id} to #{link_target}")
                logger.warning(
                    "divoid_create_node node_id=%d step=link target=%d err=%s",
                    node_id, link_target, code,
                )
                return {
                    "isError": True,
                    "content": make_error_content(
                        "partial_state",
                        f"Node #{node_id} was created (content_posted={content_posted}) "
                        f"but linking to #{link_target} failed: {code}: {msg}. "
                        f"Links created so far: {links_created}. Repair manually or delete node.",
                    ),
                }

            if not link_result.ok:
                code, msg = map_http_error(
                    link_result.status, link_result.body, config.api_key,
                    f"link node #{node_id} to #{link_target}",
                )
                logger.warning(
                    "divoid_create_node node_id=%d step=link target=%d err=%s status=%d",
                    node_id, link_target, code, link_result.status,
                )
                return {
                    "isError": True,
                    "content": make_error_content(
                        "partial_state",
                        f"Node #{node_id} was created (content_posted={content_posted}) "
                        f"but linking to #{link_target} failed ({link_result.status}): {msg}. "
                        f"Links created so far: {links_created}. Repair manually or delete node.",
                    ),
                }

            links_created.append(link_target)
            logger.info("divoid_create_node node_id=%d linked to %d", node_id, link_target)

        logger.info(
            "divoid_create_node node_id=%d ok type=%s links=%s content_length=%d",
            node_id, type, links_created, content_length,
        )

        return {
            "id": node_id,
            "type": type,
            "name": name,
            "status": status,
            "extra_links_attached": links_created,
            "content_length": content_length,
        }
