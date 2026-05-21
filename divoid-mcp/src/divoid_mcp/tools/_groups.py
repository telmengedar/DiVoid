"""
_groups -- shared group-resolution helper for composite write tools.

All composite tools that need to locate a structural group (Tasks, Docs, …) by name
under a project share this helper. Extracting it here avoids the 95%-identical
`_resolve_tasks_group` / `_resolve_docs_group` duplication flagged in PR #83.

Usage:
    from ._groups import resolve_group

    group_id, err = await resolve_group(project_id, "Tasks", config)
    if err is not None:
        # err is a human-readable string starting with a stable error code token
        return {"isError": True, "content": make_error_content("group_not_found", err)}

The helper is internal (underscore prefix). Callers in tools/ import it directly;
nothing in the public API surface exposes it.
"""

from __future__ import annotations

import logging
from typing import TYPE_CHECKING

from .. import http_client
from ..errors import map_http_error, map_unreachable

if TYPE_CHECKING:
    from ..config import DivoidConfig

logger = logging.getLogger(__name__)


async def resolve_group(
    project_id: int,
    group_name: str,
    config: "DivoidConfig",
) -> tuple[int | None, str | None]:
    """
    Resolve a named structural group under a project by walking the graph.

    Uses the path query:
        GET /nodes?path=[id:<project_id>]/[name:<group_name>]

    Returns:
        (group_id, None)       on exactly-one match.
        (None, error_message)  on zero matches, multiple matches, or HTTP failure.
                               error_message starts with a stable error-code token
                               (e.g. "group_not_found: ...") for caller use.

    Raises nothing — all failure modes are encoded in the returned error string.
    Fail-loudly on multiple matches so naming-convention drift is surfaced
    immediately rather than silently using the wrong group.
    """
    path_query = f"[id:{project_id}]/[name:{group_name}]"
    try:
        result = await http_client.get("nodes", params={"path": path_query, "count": 5})
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, f"resolve {group_name} group")
        logger.warning("resolve_group project_id=%d group=%r err=%s", project_id, group_name, code)
        return None, f"{code}: {msg}"

    if not result.ok:
        code, msg = map_http_error(
            result.status, result.body, config.api_key, f"resolve {group_name} group"
        )
        logger.warning(
            "resolve_group project_id=%d group=%r status=%d err=%s",
            project_id, group_name, result.status, code,
        )
        return None, f"{code}: {msg}"

    try:
        data = result.json()
        nodes = data.get("result", [])
    except Exception as exc:
        return None, f"Failed to parse group resolution response: {exc}"

    if len(nodes) == 0:
        msg = (
            f"group_not_found: No '{group_name}' group found for project #{project_id}. "
            f"Walk the project's adjacency to confirm the group exists "
            f"(GET /api/nodes?linkedto={project_id}). If no '{group_name}' group exists, "
            f"create one per DiVoid #493 §2."
        )
        logger.info("resolve_group project_id=%d group=%r: zero matches", project_id, group_name)
        return None, msg

    if len(nodes) > 1:
        ids = [n.get("id") for n in nodes]
        msg = (
            f"group_not_found: Path query returned {len(nodes)} matches for "
            f"[id:{project_id}]/[name:{group_name}] — expected exactly 1. ids={ids}. "
            "This indicates duplicate group structure; fix manually."
        )
        logger.warning(
            "resolve_group project_id=%d group=%r: multiple matches ids=%s",
            project_id, group_name, ids,
        )
        return None, msg

    group_id = nodes[0].get("id")
    if not isinstance(group_id, int):
        return None, (
            f"group_not_found: Group node id is not an integer: {group_id!r} "
            f"(project #{project_id}, group '{group_name}')"
        )

    logger.debug("resolve_group project_id=%d group=%r -> group_id=%d", project_id, group_name, group_id)
    return group_id, None
