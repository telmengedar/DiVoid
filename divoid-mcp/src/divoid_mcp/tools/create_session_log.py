"""
divoid_create_session_log -- composite tool: create node + post content + link to Docs group.

From the caller's perspective this is a single atomic operation. Under the hood it
makes 3-4 HTTP calls:
  1. GET /nodes?path=[id:<project_id>]/[name:Docs]  (only if project_id is given)
  2. POST /nodes                                      -- create the session-log node
  3. POST /nodes/{id}/content                         -- set the content body
  4. POST /nodes/{id}/links                           -- link to Docs group
     (plus extra_links, one call each)

Content is always required for session-logs (no 'new' escape, per DiVoid #493 §4).
Session-logs have no status lifecycle (per #493 §5) — the status field is never set.

Partial failure semantics (per architecture §6.3): if step 2 succeeds but step 3 or 4
fails, the server does NOT roll back. It returns an MCP error naming the surviving
node id and the missing step so the caller can repair manually.

Per DiVoid #493 §3, session-logs live under the Docs group of their project.
The `docs_group_id` parameter name is deliberate — it makes the target self-documenting
rather than a generic `group_id` whose destination would be ambiguous.

Architecture reference: §8.5, §6.3; structural conventions: DiVoid #493 §3/§4/§5.
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
Create a session-log node atomically: creates the node, sets its content, and links \
it to the project's Docs group — all in one call. Use this to record the narrative of \
a work arc: what was investigated, what was tried, what worked, what failed, and what \
the next agent should know. Session-logs are the memory of the hivemind (DiVoid #190 \
Rule 3) — file them at the end of any non-trivial arc, not just when things go wrong. \
Content is always required (there is no 'new' escape for session-logs per #493 §4 — \
do not create the node until you have the narrative). Session-logs have no status \
lifecycle (per #493 §5). \
Link to every node the arc touched using extra_links — tasks, bugs, documentation, \
PRs, research nodes. The links are what make the session-log findable from any of the \
nodes it references. The Docs group is resolved from project_id by walking the graph; \
alternatively supply docs_group_id directly if you already know it (e.g. DiVoid Docs = 7).\
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
    The invariant guard is the sole enforcement layer for these constraints —
    FastMCP exposes the parameters as plain {"type": "string"} in the JSON
    Schema without minLength or oneOf; enforcement is entirely here.
    """
    # Mutual exclusion: exactly one of project_id / docs_group_id must be given.
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

    # Content must be non-empty and non-whitespace (per DiVoid #493 §4).
    if not content or not content.strip():
        raise InvariantViolation(
            "content_whitespace_only",
            "Session-log content must be non-empty and non-whitespace (per #493 §4). "
            "A session-log node with no meaningful content is structurally invalid. "
            "Do not create the node until you have the narrative.",
        )


async def _execute(
    name: str,
    content: str,
    config: "DivoidConfig",
    project_id: int | None = None,
    docs_group_id: int | None = None,
    extra_links: list[int] | None = None,
) -> dict[str, Any]:
    """
    Core implementation of divoid_create_session_log.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.

    Callers must run _check_invariants() before calling this function, or pass
    validated inputs. The function does not re-run the invariant guard.
    """
    if extra_links is None:
        extra_links = []

    logger.info(
        "divoid_create_session_log name=%r project_id=%s docs_group_id=%s",
        name[:60], project_id, docs_group_id,
    )

    # --- Step 1: Resolve Docs group (only when project_id is given) ---
    resolved_group_id: int
    if project_id is not None:
        group_id, err = await resolve_group(project_id, "Docs", config)
        if err is not None:
            logger.warning("divoid_create_session_log group_resolution_failed: %s", err)
            return {"isError": True, "content": make_error_content("docs_group_not_found", err)}
        resolved_group_id = group_id  # type: ignore[assignment]
    else:
        resolved_group_id = docs_group_id  # type: ignore[assignment]

    # --- Step 2: Create the node ---
    # session-log nodes have null status — do not pass status.
    node_body: dict[str, Any] = {"name": name, "type": "session-log"}
    try:
        create_result = await http_client.post_json("nodes", node_body)
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, "create session-log node")
        logger.warning("divoid_create_session_log step=create_node err=%s", code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not create_result.ok:
        code, msg = map_http_error(
            create_result.status, create_result.body, config.api_key, "create session-log node"
        )
        logger.info(
            "divoid_create_session_log step=create_node err=%s status=%d",
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
                f"create session-log node: Could not parse response: {exc}",
            ),
        }

    logger.info("divoid_create_session_log node_id=%d created", node_id)

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
            "divoid_create_session_log node_id=%d step=post_content err=%s", node_id, code
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
            "divoid_create_session_log node_id=%d step=post_content err=%s status=%d",
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
        "divoid_create_session_log node_id=%d content_posted byte_length=%d",
        node_id, content_length,
    )

    # --- Step 4: Link to Docs group and extra_links ---
    links_created: list[int] = []
    all_link_targets = [resolved_group_id] + [lid for lid in extra_links if lid != resolved_group_id]

    for link_target in all_link_targets:
        try:
            link_result = await http_client.post_json(f"nodes/{node_id}/links", link_target)
        except http_client.DiVoidUnreachable as exc:
            code, msg = map_unreachable(exc, config.api_key, f"link node #{node_id} to #{link_target}")
            logger.warning(
                "divoid_create_session_log node_id=%d step=link target=%d err=%s",
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
                "divoid_create_session_log node_id=%d step=link target=%d err=%s status=%d",
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
        logger.info("divoid_create_session_log node_id=%d linked to %d", node_id, link_target)

    logger.info(
        "divoid_create_session_log node_id=%d ok links=%s content_length=%d",
        node_id, links_created, content_length,
    )

    return {
        "id": node_id,
        "type": "session-log",
        "name": name,
        "docs_group_id": resolved_group_id,
        "extra_links_attached": [lid for lid in links_created if lid != resolved_group_id],
        "content_length": content_length,
    }


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_create_session_log(
        name: str,
        content: str,
        project_id: int | None = None,
        docs_group_id: int | None = None,
        extra_links: list[int] | None = None,
    ) -> dict[str, Any]:
        """
        Create a session-log node in DiVoid atomically.

        Args:
            name: Search-friendly title for this session log (required, min 1 char).
                  Include keywords that describe the arc, the problem area, or what
                  was learned — a future agent should be able to find this via query.
                  Example: 'Backend: embedding-regen SQL refactor arc 2026-05-21'.
            content: The session narrative (required, must be non-empty, markdown).
                     Include: what was investigated, what was tried, what worked,
                     what failed, key decisions made, and what the next agent should
                     know. Per DiVoid #493 §4, a content-empty session-log is
                     structurally invalid. Enforcement is by the invariant guard
                     (not JSON Schema — FastMCP exposes content as plain string).
            project_id: The id of the project whose Docs group this session-log
                        belongs to. Resolved via [id:<project_id>]/[name:Docs].
                        Mutually exclusive with docs_group_id (invariant guard).
            docs_group_id: Direct id of the Docs group node. Use when you already
                           know it (e.g. DiVoid Docs = 7). Mutually exclusive with
                           project_id (invariant guard).
            extra_links: Node ids of every node the arc touched (tasks, bugs,
                         documentation, related research). Link liberally — this is
                         what makes the session-log discoverable from the nodes it
                         references. The Docs group link is always added automatically.
        """
        # --- Invariant guard (before any HTTP call) ---
        try:
            _check_invariants(name, content, project_id, docs_group_id)
        except InvariantViolation as exc:
            logger.debug("divoid_create_session_log invariant violation: %s", exc.code)
            return {"isError": True, "content": make_error_content(exc.code, exc.message)}

        return await _execute(
            name=name,
            content=content,
            config=config,
            project_id=project_id,
            docs_group_id=docs_group_id,
            extra_links=extra_links,
        )
