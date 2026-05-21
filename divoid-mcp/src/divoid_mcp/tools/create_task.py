"""
divoid_create_task -- composite tool: create node + post content + link to Tasks group.

From the caller's perspective this is a single atomic operation. Under the hood it
makes 3-4 HTTP calls:
  1. GET /nodes?path=[id:<project_id>]/[name:Tasks]  (only if project_id is given)
  2. POST /nodes                                       -- create the task node
  3. POST /nodes/{id}/content                          -- set the content body
  4. POST /nodes/{id}/links                            -- link to Tasks group
     (plus extra_links, one call each)

Partial failure semantics (per architecture §6.3): if step 2 succeeds but step 3 or 4
fails, the server does NOT roll back. It returns an MCP error that names the surviving
node id and the missing step so the caller can repair manually.

Invariant guard (before any HTTP call):
  - If status != "new" and content is missing/empty -> content_required
  - Both project_id and tasks_group_id provided -> mutually_exclusive_link_target (schema)
  - Neither provided -> no_link_target (schema)
  - status not in allowed enum -> task_status_not_in_lifecycle (schema first, guard second)

Architecture reference: §8.4, §6.3
"""

from __future__ import annotations

import logging
from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import InvariantViolation, make_error_content, map_http_error, map_unreachable

logger = logging.getLogger(__name__)

_ALLOWED_STATUSES = {"new", "open", "in-progress", "closed"}

_TOOL_DESCRIPTION = """\
Create a task node atomically: creates the node, sets its content, and links it to \
the project's Tasks group — all in one call. Use this for any new work item. \
Content is required unless status="new" (the "quick capture" lifecycle stage per \
DiVoid structural conventions #493 §4); for any other status the content body must \
be provided and non-empty. Use "new" only for one-line jot-down captures that you \
intend to enrich later. The Tasks group is resolved from project_id by walking the \
graph; if your project does not have a Tasks group yet, this tool returns an error \
and you will need to create the group first. Alternatively, supply tasks_group_id \
directly if you already know it (e.g. DiVoid Tasks = 314).\
"""


def _check_invariants(
    name: str,
    content: str | None,
    status: str,
    project_id: int | None,
    tasks_group_id: int | None,
) -> None:
    """
    Check runtime invariants before making any HTTP call.

    Raises InvariantViolation with a stable code if any invariant is broken.
    The JSON Schema handles the oneOf (project_id XOR tasks_group_id) and
    enum validation; these guards are belt-and-suspenders and produce better
    error messages when the schema is not strictly enforced by the runtime.
    """
    # Belt-and-suspenders for the schema's oneOf constraint.
    if project_id is not None and tasks_group_id is not None:
        raise InvariantViolation(
            "mutually_exclusive_link_target",
            "Provide either project_id or tasks_group_id, not both. "
            "project_id resolves the Tasks group by walking the graph; "
            "tasks_group_id uses the group directly.",
        )
    if project_id is None and tasks_group_id is None:
        raise InvariantViolation(
            "no_link_target",
            "Either project_id or tasks_group_id is required. "
            "Provide project_id to resolve the Tasks group automatically, "
            "or provide tasks_group_id directly (e.g. DiVoid Tasks = 314).",
        )

    # Belt-and-suspenders for the schema's enum constraint.
    if status not in _ALLOWED_STATUSES:
        raise InvariantViolation(
            "task_status_not_in_lifecycle",
            f"Status '{status}' is not a valid task status. "
            f"Allowed values: {', '.join(sorted(_ALLOWED_STATUSES))}. "
            "See DiVoid #493 §5 for the task lifecycle.",
        )

    # The load-bearing content-required check — JSON Schema cannot express this
    # (status-conditional required) reliably across all MCP runtimes.
    if status != "new" and (not content or not content.strip()):
        raise InvariantViolation(
            "content_required",
            f"Task content is required unless status='new' (per DiVoid #493 §4). "
            f"Current status='{status}'. Either provide content describing the current "
            "state, what is missing, and suggested order of work — or set status='new' "
            "explicitly and enrich the node afterwards.",
        )


async def _resolve_tasks_group(project_id: int, config: DivoidConfig) -> tuple[int | None, str | None]:
    """
    Resolve the Tasks group id for a given project by walking the graph.

    Returns (group_id, None) on success, or (None, error_message) on failure.
    Uses the path query: GET /nodes?path=[id:<project_id>]/[name:Tasks]
    """
    path_query = f"[id:{project_id}]/[name:Tasks]"
    try:
        result = await http_client.get("nodes", params={"path": path_query, "count": 5})
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, "resolve Tasks group")
        return None, f"{code}: {msg}"

    if not result.ok:
        code, msg = map_http_error(result.status, result.body, config.api_key, "resolve Tasks group")
        return None, f"{code}: {msg}"

    try:
        data = result.json()
        nodes = data.get("result", [])
    except Exception as exc:
        return None, f"Failed to parse group resolution response: {exc}"

    if len(nodes) == 0:
        return None, (
            f"tasks_group_not_found: No Tasks group found for project #{project_id}. "
            "Walk the project's adjacency to confirm the group exists "
            "(GET /api/nodes?linkedto=<project_id>). If no Tasks group exists, "
            "create one per DiVoid #493 §2."
        )

    if len(nodes) > 1:
        ids = [n.get("id") for n in nodes]
        return None, (
            f"tasks_group_not_found: Path query returned {len(nodes)} matches for "
            f"[id:{project_id}]/[name:Tasks] — expected exactly 1. ids={ids}. "
            "This indicates duplicate group structure; fix manually."
        )

    group_id = nodes[0].get("id")
    if not isinstance(group_id, int):
        return None, f"tasks_group_not_found: Group node id is not an integer: {group_id!r}"

    return group_id, None


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_create_task(
        name: str,
        project_id: int | None = None,
        tasks_group_id: int | None = None,
        content: str | None = None,
        status: str = "open",
        extra_links: list[int] | None = None,
    ) -> dict[str, Any]:
        """
        Create a task node in DiVoid atomically.

        Args:
            name: Search-friendly task title (required, min 1 char, max 256 chars).
                  Lead with an area prefix when scope is obvious
                  (e.g. 'Backend: ...', 'Frontend: ...').
            project_id: The id of the project node whose Tasks group this task
                        belongs to. The tool resolves the Tasks group by walking
                        [id:<project_id>]/[name:Tasks]. Mutually exclusive with
                        tasks_group_id.
            tasks_group_id: Direct id of the Tasks group node, bypassing the
                            project-to-group lookup. Use when you already know the
                            group id (e.g. DiVoid Tasks = 314). Mutually exclusive
                            with project_id.
            content: Task scope: current state, what is missing, suggested order of
                     work. Required UNLESS status='new'. If status='new' and content
                     is empty, the task is a quick-capture jot to enrich later.
            status: Task lifecycle status ('new', 'open', 'in-progress', 'closed').
                    Defaults to 'open'. Use 'new' only for content-not-yet-written
                    captures.
            extra_links: Additional node ids to link the new task to (e.g. parent
                         task, related documentation). The Tasks group link is always
                         added automatically.
        """
        if extra_links is None:
            extra_links = []

        # --- Invariant guard (before any HTTP call) ---
        try:
            _check_invariants(name, content, status, project_id, tasks_group_id)
        except InvariantViolation as exc:
            logger.debug("divoid_create_task invariant violation: %s", exc.code)
            return {"isError": True, "content": make_error_content(exc.code, exc.message)}

        logger.info(
            "divoid_create_task name=%r status=%s project_id=%s tasks_group_id=%s",
            name[:60], status, project_id, tasks_group_id,
        )

        # --- Step 1: Resolve Tasks group (only when project_id is given) ---
        resolved_group_id: int
        if project_id is not None:
            group_id, err = await _resolve_tasks_group(project_id, config)
            if err is not None:
                logger.warning("divoid_create_task group_resolution_failed: %s", err)
                return {"isError": True, "content": make_error_content("tasks_group_not_found", err)}
            resolved_group_id = group_id  # type: ignore[assignment]
        else:
            resolved_group_id = tasks_group_id  # type: ignore[assignment]

        # --- Step 2: Create the node ---
        node_body: dict[str, Any] = {"name": name, "type": "task", "status": status}
        try:
            create_result = await http_client.post_json("nodes", node_body)
        except http_client.DiVoidUnreachable as exc:
            code, msg = map_unreachable(exc, config.api_key, "create task node")
            logger.warning("divoid_create_task step=create_node err=%s", code)
            return {"isError": True, "content": make_error_content(code, msg)}

        if not create_result.ok:
            code, msg = map_http_error(create_result.status, create_result.body, config.api_key, "create task node")
            logger.info("divoid_create_task step=create_node err=%s status=%d", code, create_result.status)
            return {"isError": True, "content": make_error_content(code, msg)}

        try:
            node_data = create_result.json()
            node_id: int = node_data["id"]
        except Exception as exc:
            return {
                "isError": True,
                "content": make_error_content(
                    "divoid_bad_request",
                    f"create task node: Could not parse response: {exc}",
                ),
            }

        logger.info("divoid_create_task node_id=%d created", node_id)

        # --- Step 3: Post content (skip if status=new and no content) ---
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
                logger.warning("divoid_create_task node_id=%d step=post_content err=%s", node_id, code)
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
                    "divoid_create_task node_id=%d step=post_content err=%s status=%d",
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
            logger.info("divoid_create_task node_id=%d content_posted byte_length=%d", node_id, content_length)

        # --- Step 4: Link to Tasks group ---
        links_created: list[int] = []
        all_link_targets = [resolved_group_id] + [lid for lid in extra_links if lid != resolved_group_id]

        for link_target in all_link_targets:
            try:
                link_result = await http_client.post_json(f"nodes/{node_id}/links", link_target)
            except http_client.DiVoidUnreachable as exc:
                code, msg = map_unreachable(exc, config.api_key, f"link node #{node_id} to #{link_target}")
                logger.warning(
                    "divoid_create_task node_id=%d step=link target=%d err=%s",
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
                    "divoid_create_task node_id=%d step=link target=%d err=%s status=%d",
                    node_id, link_target, code, link_result.status,
                )
                is_tasks_group = (link_target == resolved_group_id)
                return {
                    "isError": True,
                    "content": make_error_content(
                        "partial_state",
                        f"Node #{node_id} was created (content_posted={content_posted}) "
                        f"but linking to #{link_target} "
                        f"({'Tasks group' if is_tasks_group else 'extra link'}) "
                        f"failed ({link_result.status}): {msg}. "
                        f"Links created so far: {links_created}. Repair manually or delete node.",
                    ),
                }

            links_created.append(link_target)
            logger.info("divoid_create_task node_id=%d linked to %d", node_id, link_target)

        logger.info(
            "divoid_create_task node_id=%d ok links=%s content_length=%d",
            node_id, links_created, content_length,
        )

        return {
            "id": node_id,
            "type": "task",
            "name": name,
            "status": status,
            "tasks_group_id": resolved_group_id,
            "extra_links_attached": [lid for lid in links_created if lid != resolved_group_id],
            "content_length": content_length,
        }
