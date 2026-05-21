"""
divoid_create_documentation -- composite tool: create node + post content + link to Docs group.

From the caller's perspective this is a single atomic operation. Under the hood it
makes 3-4 HTTP calls:
  1. GET /nodes?path=[id:<project_id>]/[name:Docs]  (only if project_id is given)
  2. POST /nodes                                      -- create the documentation node
  3. POST /nodes/{id}/content                         -- set the content body
  4. POST /nodes/{id}/links                           -- link to Docs group
     (plus extra_links, one call each)

Content is always required for documentation (no 'new' escape). FastMCP exposes
content as a plain {"type": "string"} parameter without minLength or required
enforcement — the invariant guard is the sole enforcement layer (catches both
missing and whitespace-only content per DiVoid #493 §4).

Partial failure semantics (per architecture §6.3): if step 2 succeeds but step 3 or 4
fails, the server does NOT roll back. It returns an MCP error naming the surviving
node id and the missing step so the caller can repair manually.

Architecture reference: §8.5, §6.3
"""

from __future__ import annotations

import logging
from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import InvariantViolation, make_error_content, map_http_error, map_unreachable
from ._groups import resolve_group

logger = logging.getLogger(__name__)

_TOOL_DESCRIPTION = """\
Create a documentation node atomically: creates the node, sets its content, and links \
it to the project's Docs group — all in one call. Use this for design docs, \
architectural notes, gotchas, tutorials, anti-patterns, closure notes — anything that \
is reusable knowledge per DiVoid #190 Rule 2. Content is always required (there is no \
'new' escape for documentation per #493 §4 — do not create a documentation node until \
you have the document). Documentation nodes have no status field. The Docs group is \
resolved from project_id by walking the graph; alternatively supply docs_group_id \
directly if you already know it (e.g. DiVoid Docs = 7).\
"""


def _check_invariants(
    name: str,
    content: str,
    project_id: int | None,
    docs_group_id: int | None,
) -> None:
    """
    Check runtime invariants before making any HTTP call.

    Raises InvariantViolation with a stable code if any invariant is broken.
    The invariant guard is the sole enforcement layer for all constraints —
    FastMCP exposes parameters as plain {"type": "string"} in the JSON Schema
    without minLength, oneOf, or required enforcement; enforcement is entirely here.
    """
    if project_id is not None and docs_group_id is not None:
        raise InvariantViolation(
            "mutually_exclusive_link_target",
            "Provide either project_id or docs_group_id, not both. "
            "project_id resolves the Docs group by walking the graph; "
            "docs_group_id uses the group directly.",
        )
    if project_id is None and docs_group_id is None:
        raise InvariantViolation(
            "no_link_target",
            "Either project_id or docs_group_id is required. "
            "Provide project_id to resolve the Docs group automatically, "
            "or provide docs_group_id directly (e.g. DiVoid Docs = 7).",
        )

    # Whitespace-only content is structurally invalid (per #493 §4 — a doc of
    # just spaces is not a document). FastMCP does not enforce this in schema.
    if not content or not content.strip():
        raise InvariantViolation(
            "content_whitespace_only",
            "Documentation content must be non-empty and non-whitespace (per #493 §4). "
            "A documentation node with no meaningful content is structurally invalid. "
            "Do not create the node until you have the document.",
        )


async def _resolve_docs_group(project_id: int, config: DivoidConfig) -> tuple[int | None, str | None]:
    """
    Resolve the Docs group id for a given project.

    Thin wrapper around the shared `resolve_group` helper so external callers
    (smoke tests) that import this name directly keep working after the refactor.
    """
    return await resolve_group(project_id, "Docs", config)


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_create_documentation(
        name: str,
        content: str,
        project_id: int | None = None,
        docs_group_id: int | None = None,
        extra_links: list[int] | None = None,
    ) -> dict[str, Any]:
        """
        Create a documentation node in DiVoid atomically.

        Args:
            name: Search-friendly title (required).
                  Include keywords a future agent would query for.
            content: The document body (required, must be non-empty, markdown).
                     Per DiVoid #493 §4 a content-empty documentation node is
                     structurally invalid. Do not create the node until you have
                     the content ready. The invariant guard is the sole enforcement
                     layer (FastMCP exposes content as plain string, no minLength).
            project_id: The id of the project whose Docs group this documentation
                        belongs to. Resolved via [id:<project_id>]/[name:Docs].
                        Mutually exclusive with docs_group_id.
            docs_group_id: Direct id of the Docs group node. Use when you already
                           know it (e.g. DiVoid Docs = 7). Mutually exclusive with
                           project_id.
            extra_links: Additional node ids to link the documentation to (e.g. the
                         task it was produced for, related siblings, the project
                         itself). The Docs group link is always added automatically.
        """
        if extra_links is None:
            extra_links = []

        # --- Invariant guard (before any HTTP call) ---
        try:
            _check_invariants(name, content, project_id, docs_group_id)
        except InvariantViolation as exc:
            logger.debug("divoid_create_documentation invariant violation: %s", exc.code)
            return {"isError": True, "content": make_error_content(exc.code, exc.message)}

        logger.info(
            "divoid_create_documentation name=%r project_id=%s docs_group_id=%s",
            name[:60], project_id, docs_group_id,
        )

        # --- Step 1: Resolve Docs group (only when project_id is given) ---
        resolved_group_id: int
        if project_id is not None:
            group_id, err = await _resolve_docs_group(project_id, config)
            if err is not None:
                logger.warning("divoid_create_documentation group_resolution_failed: %s", err)
                return {"isError": True, "content": make_error_content("docs_group_not_found", err)}
            resolved_group_id = group_id  # type: ignore[assignment]
        else:
            resolved_group_id = docs_group_id  # type: ignore[assignment]

        # --- Step 2: Create the node ---
        # documentation nodes have null status — do not pass status.
        node_body: dict[str, Any] = {"name": name, "type": "documentation"}
        try:
            create_result = await http_client.post_json("nodes", node_body)
        except http_client.DiVoidUnreachable as exc:
            code, msg = map_unreachable(exc, config.api_key, "create documentation node")
            logger.warning("divoid_create_documentation step=create_node err=%s", code)
            return {"isError": True, "content": make_error_content(code, msg)}

        if not create_result.ok:
            code, msg = map_http_error(
                create_result.status, create_result.body, config.api_key, "create documentation node"
            )
            logger.info(
                "divoid_create_documentation step=create_node err=%s status=%d",
                code, create_result.status,
            )
            return {"isError": True, "content": make_error_content(code, msg)}

        try:
            node_data = create_result.json()
            node_id: int = node_data["id"]
        except Exception as exc:
            return {
                "isError": True,
                "content": make_error_content(
                    "divoid_bad_request",
                    f"create documentation node: Could not parse response: {exc}",
                ),
            }

        logger.info("divoid_create_documentation node_id=%d created", node_id)

        # --- Step 3: Post content ---
        content_bytes = content.encode("utf-8")
        try:
            content_result = await http_client.post_bytes(
                f"nodes/{node_id}/content",
                content_bytes,
                "text/markdown; charset=utf-8",
            )
        except http_client.DiVoidUnreachable as exc:
            code, msg = map_unreachable(exc, config.api_key, f"post content for node #{node_id}")
            logger.warning(
                "divoid_create_documentation node_id=%d step=post_content err=%s", node_id, code
            )
            return {
                "isError": True,
                "content": make_error_content(
                    "partial_state",
                    f"Node #{node_id} was created successfully but content POST failed: "
                    f"{code}: {msg}. The node is orphaned and content-empty; repair manually "
                    f"(POST /api/nodes/{node_id}/content) or delete it.",
                ),
            }

        if not content_result.ok:
            code, msg = map_http_error(
                content_result.status, content_result.body, config.api_key,
                f"post content for node #{node_id}",
            )
            logger.warning(
                "divoid_create_documentation node_id=%d step=post_content err=%s status=%d",
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

        content_length = len(content_bytes)
        logger.info(
            "divoid_create_documentation node_id=%d content_posted byte_length=%d",
            node_id, content_length,
        )

        # --- Step 4: Link to Docs group ---
        links_created: list[int] = []
        all_link_targets = [resolved_group_id] + [lid for lid in extra_links if lid != resolved_group_id]

        for link_target in all_link_targets:
            try:
                link_result = await http_client.post_json(f"nodes/{node_id}/links", link_target)
            except http_client.DiVoidUnreachable as exc:
                code, msg = map_unreachable(exc, config.api_key, f"link node #{node_id} to #{link_target}")
                logger.warning(
                    "divoid_create_documentation node_id=%d step=link target=%d err=%s",
                    node_id, link_target, code,
                )
                return {
                    "isError": True,
                    "content": make_error_content(
                        "partial_state",
                        f"Node #{node_id} was created (content_posted=True, "
                        f"content_length={content_length}) "
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
                    "divoid_create_documentation node_id=%d step=link target=%d err=%s status=%d",
                    node_id, link_target, code, link_result.status,
                )
                is_docs_group = (link_target == resolved_group_id)
                return {
                    "isError": True,
                    "content": make_error_content(
                        "partial_state",
                        f"Node #{node_id} was created (content_posted=True, "
                        f"content_length={content_length}) "
                        f"but linking to #{link_target} "
                        f"({'Docs group' if is_docs_group else 'extra link'}) "
                        f"failed ({link_result.status}): {msg}. "
                        f"Links created so far: {links_created}. Repair manually or delete node.",
                    ),
                }

            links_created.append(link_target)
            logger.info("divoid_create_documentation node_id=%d linked to %d", node_id, link_target)

        logger.info(
            "divoid_create_documentation node_id=%d ok links=%s content_length=%d",
            node_id, links_created, content_length,
        )

        return {
            "id": node_id,
            "type": "documentation",
            "name": name,
            "docs_group_id": resolved_group_id,
            "extra_links_attached": [lid for lid in links_created if lid != resolved_group_id],
            "content_length": content_length,
        }
