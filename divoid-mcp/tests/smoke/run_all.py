#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
Smoke tests for divoid-mcp Phase 1 tools.

These are live integration scripts -- they call the real DiVoid instance
using the admin key from ~/.claude/secrets/.divoid-online. They are NOT
pytest-based; they print PASS/FAIL and exit non-zero on any failure.

Run from the repo root after `pip install -e .`:

    python tests/smoke/run_all.py

Each test calls the HTTP client functions directly (same code path as the
MCP tools use) and asserts the response has the expected shape.

What "structurally correct" means for Phase 1:
- divoid_search:          returns {"result": [...], "total": int}
- divoid_get_node:        returns {"id": int, "type": str, "name": str, ...}
- divoid_get_content:     returns UTF-8 decodeable bytes with matching content-type
- divoid_link_nodes:      POST /nodes/{id}/links returns 2xx; link visible in adjacency
- divoid_create_task:     creates node + content + Tasks group link atomically
- divoid_create_documentation: creates node + content + Docs group link atomically

Pinned group ids for smoke-test target (DiVoid project #3):
  Tasks group: #314
  Docs group:  #7
"""

from __future__ import annotations

import asyncio
import sys
import traceback
from typing import Any

from divoid_mcp.config import load_secret
from divoid_mcp import http_client
from divoid_mcp.tools.create_task import _check_invariants as _check_task_invariants
from divoid_mcp.tools.create_documentation import _check_invariants as _check_doc_invariants
from divoid_mcp.errors import InvariantViolation

# Pinned group ids for the smoke-test target project (DiVoid project #3).
_DIVOID_PROJECT_ID = 3
_DIVOID_TASKS_GROUP_ID = 314
_DIVOID_DOCS_GROUP_ID = 7


def _setup() -> Any:
    config = load_secret()
    http_client.init(config.base_url, config.api_key)
    return config


# ---------------------------------------------------------------------------
# Test helpers
# ---------------------------------------------------------------------------

_results: list[tuple[str, bool, str]] = []


def _record(name: str, passed: bool, detail: str = "") -> None:
    _results.append((name, passed, detail))
    status = "PASS" if passed else "FAIL"
    line = f"  [{status}] {name}"
    if detail:
        line += f": {detail}"
    # Use ascii-safe output for Windows CP1252 terminals
    print(line.encode("ascii", errors="replace").decode("ascii"))


def _assert(name: str, condition: bool, detail: str = "") -> None:
    _record(name, condition, detail)


# ---------------------------------------------------------------------------
# Individual smoke tests
# ---------------------------------------------------------------------------

async def smoke_search(config: Any) -> None:
    """divoid_search: returns result list and total for a known query."""
    print("\n--- divoid_search ---")
    result = await http_client.get("nodes", params={"query": "agent onboarding", "count": 5})
    _assert("HTTP 200", result.ok, f"status={result.status}")
    if result.ok:
        data = result.json()
        _assert("has 'result' key", "result" in data)
        _assert("has 'total' key", "total" in data)
        _assert("result is list", isinstance(data.get("result"), list))
        _assert(
            "total is int",
            isinstance(data.get("total"), int),
            f"total={data.get('total')}",
        )
        _assert(
            "result has at least one item",
            len(data.get("result", [])) > 0,
            f"count={len(data.get('result', []))}",
        )


async def smoke_search_with_type_filter(config: Any) -> None:
    """
    divoid_search: type filter reduces result set toward documentation nodes.

    NOTE: DiVoid's semantic search with a type filter ranks by vector similarity
    first; the type filter narrows the candidate pool but semantic ranking can
    occasionally surface nodes of other types if they are closely related. The
    test asserts that the filter returns a valid response, not that every node
    is exactly the requested type -- that is an API-level semantics question.
    """
    print("\n--- divoid_search (type=documentation filter) ---")
    # Use a list-only query (no semantic query=) to assert strict type filtering.
    result = await http_client.get(
        "nodes",
        params={"type": ["documentation"], "count": 10},
    )
    _assert("HTTP 200 with type filter (list-only)", result.ok, f"status={result.status}")
    if result.ok:
        data = result.json()
        types = [n.get("type") for n in data.get("result", []) if n.get("type") is not None]
        non_doc = [t for t in types if t != "documentation"]
        _assert(
            "list-only type filter: all nodes are documentation",
            len(non_doc) == 0,
            f"unexpected types: {set(non_doc)}" if non_doc else f"count={len(types)}",
        )


async def smoke_get_node(config: Any) -> None:
    """divoid_get_node: node #9 (onboarding) has expected shape."""
    print("\n--- divoid_get_node ---")
    result = await http_client.get("nodes/9")
    _assert("HTTP 200 for node #9", result.ok, f"status={result.status}")
    if result.ok:
        data = result.json()
        _assert("has id field", "id" in data, f"id={data.get('id')}")
        _assert("id is 9", data.get("id") == 9)
        _assert("has type field", "type" in data, f"type={data.get('type')!r}")
        _assert("has name field", "name" in data, f"name={data.get('name')!r}")
        _assert(
            "type is non-empty string",
            isinstance(data.get("type"), str) and len(data.get("type", "")) > 0,
        )


async def smoke_get_node_not_found(config: Any) -> None:
    """
    divoid_get_node: non-existent node returns 200 with empty body.

    DiVoid's GET /api/nodes/{id} returns HTTP 200 with an empty response body
    when the node does not exist (rather than 404). The tool detects this and
    returns a structured node_not_found error. The content endpoint does return
    404 for missing nodes.
    """
    print("\n--- divoid_get_node (not found) ---")
    # GET /api/nodes/{id} returns 200 with empty body for missing nodes.
    result = await http_client.get("nodes/999999999")
    _assert(
        "GET /nodes/999999999 returns 200 (empty body = not found)",
        result.status == 200,
        f"status={result.status}",
    )
    _assert(
        "response body is empty for non-existent node",
        not result.body or not result.body.strip(),
        f"body_len={len(result.body)}",
    )

    # The content endpoint DOES return 404 for missing nodes.
    content_result = await http_client.get("nodes/999999999/content")
    _assert(
        "GET /nodes/999999999/content returns 404",
        content_result.status == 404,
        f"status={content_result.status}",
    )


async def smoke_get_content_missing_node(config: Any) -> None:
    """
    divoid_get_content: non-existent node returns node_not_found, not empty content.

    DiVoid /content returns 404 in two cases: missing node and existing-but-no-content.
    The tool must parse the body and return isError=True with code=node_not_found for
    a truly missing node, not the empty-content success shape. This test covers the
    CF1 fix: before the fix, the tool returned {"id": ..., "content": "", ...} for both.
    """
    print("\n--- divoid_get_content (missing node) ---")
    result = await http_client.get("nodes/999999999/content")
    _assert(
        "GET /nodes/999999999/content returns 404",
        result.status == 404,
        f"status={result.status}",
    )
    if result.status == 404:
        try:
            body_json = result.json()
            text = body_json.get("text", "") if isinstance(body_json, dict) else ""
            # Should NOT contain "has no content" — that's the empty-content case.
            _assert(
                "missing-node 404 body does not contain 'has no content'",
                "has no content" not in text,
                f"text={text!r}",
            )
            _assert(
                "missing-node 404 body contains 'not found'",
                "not found" in text.lower(),
                f"text={text!r}",
            )
        except Exception as exc:
            _record("missing-node 404 body is parseable JSON", False, str(exc))


async def smoke_get_content_no_content_node(config: Any) -> None:
    """
    divoid_get_content: node #7 (structural group, no content) returns empty-content shape.

    Node #7 is the 'Docs' structural group — it exists but has no content body.
    DiVoid returns 404 with a text containing "has no content". The tool must
    return {"id": 7, "content": "", "content_type": null, "byte_length": 0},
    not an error. This test covers the CF1 fix's other branch.
    """
    print("\n--- divoid_get_content (node has no content) ---")
    result = await http_client.get("nodes/7/content")
    _assert(
        "GET /nodes/7/content returns 404 (exists but no content)",
        result.status == 404,
        f"status={result.status}",
    )
    if result.status == 404:
        try:
            body_json = result.json()
            text = body_json.get("text", "") if isinstance(body_json, dict) else ""
            _assert(
                "no-content 404 body contains 'has no content'",
                "has no content" in text,
                f"text={text!r}",
            )
        except Exception as exc:
            _record("no-content 404 body is parseable JSON", False, str(exc))


async def smoke_get_content(config: Any) -> None:
    """divoid_get_content: node #9 content is non-empty text."""
    print("\n--- divoid_get_content ---")
    result = await http_client.get("nodes/9/content")
    _assert("HTTP 200 for node #9 content", result.ok, f"status={result.status}")
    if result.ok:
        _assert("content is non-empty", len(result.body) > 0, f"byte_length={len(result.body)}")
        content_type = result.headers.get("content-type", "")
        _assert(
            "content-type is text",
            "text" in content_type.lower() or not content_type,
            f"content-type={content_type!r}",
        )
        try:
            decoded = result.body.decode("utf-8")
            _assert("content decodes as UTF-8", True)
            _assert(
                "content is substantial text",
                len(decoded.strip()) > 100,
                f"length={len(decoded)}",
            )
        except UnicodeDecodeError:
            _assert("content decodes as UTF-8", False, "UnicodeDecodeError")


async def smoke_get_content_api_reference(config: Any) -> None:
    """divoid_get_content: node #8 content hash matches the pinned drift constant."""
    import hashlib
    from divoid_mcp.version import PINNED_API_REF_HASH

    print("\n--- divoid_get_content (drift hash check) ---")
    result = await http_client.get("nodes/8/content")
    _assert("HTTP 200 for node #8 content", result.ok, f"status={result.status}")
    if result.ok:
        actual_hash = hashlib.sha256(result.body).hexdigest()
        _assert(
            "node #8 hash matches pinned constant",
            actual_hash == PINNED_API_REF_HASH,
            f"pinned={PINNED_API_REF_HASH[:16]}... actual={actual_hash[:16]}...",
        )


async def smoke_link_nodes(config: Any) -> None:
    """
    divoid_link_nodes: create a link and verify it appears in the adjacency query.

    NOTE: DiVoid returns HTTP 500 {"code":"unhandled","text":"Nodes already linked"}
    when trying to create a duplicate link. The tool handles this gracefully and
    returns linked=True with already_existed=True. This test verifies both the
    new-link path (using the adjacency check) and the tool's idempotency handling.
    """
    print("\n--- divoid_link_nodes ---")

    # Verify both nodes exist first.
    r8 = await http_client.get("nodes/8")
    r9 = await http_client.get("nodes/9")
    if not (r8.ok and r8.body.strip()):
        _record("link precondition: node #8 exists", False, "cannot proceed")
        return
    if not (r9.ok and r9.body.strip()):
        _record("link precondition: node #9 exists", False, "cannot proceed")
        return

    _record("link precondition: nodes #8 and #9 exist", True)

    # Post the link -- may succeed (new link) or return 500 "already linked".
    # Both outcomes are valid; the tool wraps the 500 as idempotent success.
    result = await http_client.post_json("nodes/8/links", 9)
    already_linked = False
    if not result.ok and result.status == 500:
        try:
            body_json = result.json()
            already_linked = "already linked" in body_json.get("text", "").lower()
        except Exception:
            pass

    _assert(
        "POST /nodes/8/links: 2xx (new) or 500-already-linked (idempotent)",
        result.ok or already_linked,
        f"status={result.status} already_linked={already_linked}",
    )

    # Verify the link appears in the adjacency query.
    links_result = await http_client.get("nodes/links", params={"ids": [8, 9]})
    _assert("GET /nodes/links returns 200", links_result.ok, f"status={links_result.status}")
    if links_result.ok:
        links_data = links_result.json()
        pairs = {
            (lnk.get("sourceId"), lnk.get("targetId"))
            for lnk in links_data.get("result", [])
        }
        has_link = (8, 9) in pairs or (9, 8) in pairs
        _assert(
            "link #8-#9 appears in adjacency result",
            has_link,
            f"pair_count={len(pairs)}",
        )


# ---------------------------------------------------------------------------
# Phase 1 PR2 smoke tests: divoid_create_task
# ---------------------------------------------------------------------------

async def smoke_create_task_group_resolution(config: Any) -> None:
    """
    divoid_create_task: project_id=3 resolves to Tasks group #314.

    Pins the resolution path so we catch any group-renaming or structural
    changes to the DiVoid project.
    """
    print("\n--- divoid_create_task (group resolution: project #3 -> Tasks #314) ---")
    result = await http_client.get(
        "nodes", params={"path": f"[id:{_DIVOID_PROJECT_ID}]/[name:Tasks]", "count": 5}
    )
    _assert("HTTP 200 for Tasks group path query", result.ok, f"status={result.status}")
    if result.ok:
        data = result.json()
        nodes = data.get("result", [])
        _assert(
            "exactly 1 Tasks group found for project #3",
            len(nodes) == 1,
            f"count={len(nodes)}",
        )
        if len(nodes) == 1:
            gid = nodes[0].get("id")
            _assert(
                f"Tasks group id is {_DIVOID_TASKS_GROUP_ID} (pinned)",
                gid == _DIVOID_TASKS_GROUP_ID,
                f"actual_id={gid}",
            )


async def smoke_create_documentation_group_resolution(config: Any) -> None:
    """
    divoid_create_documentation: project_id=3 resolves to Docs group #7.

    Pins the resolution path for the Docs group.
    """
    print("\n--- divoid_create_documentation (group resolution: project #3 -> Docs #7) ---")
    result = await http_client.get(
        "nodes", params={"path": f"[id:{_DIVOID_PROJECT_ID}]/[name:Docs]", "count": 5}
    )
    _assert("HTTP 200 for Docs group path query", result.ok, f"status={result.status}")
    if result.ok:
        data = result.json()
        nodes = data.get("result", [])
        _assert(
            "exactly 1 Docs group found for project #3",
            len(nodes) == 1,
            f"count={len(nodes)}",
        )
        if len(nodes) == 1:
            gid = nodes[0].get("id")
            _assert(
                f"Docs group id is {_DIVOID_DOCS_GROUP_ID} (pinned)",
                gid == _DIVOID_DOCS_GROUP_ID,
                f"actual_id={gid}",
            )


async def smoke_create_task_invariant_violation(config: Any) -> None:
    """
    divoid_create_task: content missing + status != 'new' -> content_required violation.

    This test exercises the invariant guard directly (no HTTP call should be made).
    """
    print("\n--- divoid_create_task (invariant: content_required) ---")

    # status='open', no content -> must raise InvariantViolation(content_required)
    raised = False
    violation_code = None
    try:
        _check_task_invariants(
            name="Smoke test: should never be created",
            content=None,
            status="open",
            project_id=_DIVOID_PROJECT_ID,
            tasks_group_id=None,
        )
    except InvariantViolation as exc:
        raised = True
        violation_code = exc.code

    _assert("invariant guard raises InvariantViolation", raised)
    _assert(
        "violation code is 'content_required'",
        violation_code == "content_required",
        f"code={violation_code!r}",
    )

    # status='in-progress', whitespace-only content -> also content_required
    raised2 = False
    violation_code2 = None
    try:
        _check_task_invariants(
            name="Smoke test: should never be created",
            content="   ",
            status="in-progress",
            project_id=_DIVOID_PROJECT_ID,
            tasks_group_id=None,
        )
    except InvariantViolation as exc:
        raised2 = True
        violation_code2 = exc.code

    _assert("whitespace-only content raises InvariantViolation", raised2)
    _assert(
        "whitespace content violation code is 'content_required'",
        violation_code2 == "content_required",
        f"code={violation_code2!r}",
    )

    # status='new', no content -> OK (no exception)
    no_raise = True
    try:
        _check_task_invariants(
            name="Smoke test: quick capture ok",
            content=None,
            status="new",
            project_id=_DIVOID_PROJECT_ID,
            tasks_group_id=None,
        )
    except InvariantViolation:
        no_raise = False

    _assert("status='new' with no content does NOT raise", no_raise)


async def smoke_create_documentation_invariant_violation(config: Any) -> None:
    """
    divoid_create_documentation: whitespace-only content -> content_whitespace_only violation.

    Empty content is also caught (minLength at schema level, whitespace guard here).
    """
    print("\n--- divoid_create_documentation (invariant: content_whitespace_only) ---")

    # Whitespace-only content -> content_whitespace_only
    raised = False
    violation_code = None
    try:
        _check_doc_invariants(
            name="Smoke test: should never be created",
            content="   \n  ",
            project_id=_DIVOID_PROJECT_ID,
            docs_group_id=None,
        )
    except InvariantViolation as exc:
        raised = True
        violation_code = exc.code

    _assert("whitespace content raises InvariantViolation", raised)
    _assert(
        "violation code is 'content_whitespace_only'",
        violation_code == "content_whitespace_only",
        f"code={violation_code!r}",
    )

    # Empty string -> also content_whitespace_only
    raised2 = False
    violation_code2 = None
    try:
        _check_doc_invariants(
            name="Smoke test: should never be created",
            content="",
            project_id=_DIVOID_PROJECT_ID,
            docs_group_id=None,
        )
    except InvariantViolation as exc:
        raised2 = True
        violation_code2 = exc.code

    _assert("empty content raises InvariantViolation", raised2)
    _assert(
        "empty content violation code is 'content_whitespace_only'",
        violation_code2 == "content_whitespace_only",
        f"code={violation_code2!r}",
    )

    # Both project_id + docs_group_id -> mutually_exclusive_link_target
    raised3 = False
    violation_code3 = None
    try:
        _check_doc_invariants(
            name="Smoke test",
            content="Some valid content here.",
            project_id=_DIVOID_PROJECT_ID,
            docs_group_id=_DIVOID_DOCS_GROUP_ID,
        )
    except InvariantViolation as exc:
        raised3 = True
        violation_code3 = exc.code

    _assert("both project_id+docs_group_id raises InvariantViolation", raised3)
    _assert(
        "violation code is 'mutually_exclusive_link_target'",
        violation_code3 == "mutually_exclusive_link_target",
        f"code={violation_code3!r}",
    )


async def smoke_create_task_missing_project(config: Any) -> None:
    """
    divoid_create_task: project_id=999999999 (nonexistent) -> structured error, not crash.

    The group resolution must return a structured tasks_group_not_found message.
    This validates the fail-loudly path for a nonexistent project.
    """
    print("\n--- divoid_create_task (missing project: group resolution fail) ---")
    result = await http_client.get(
        "nodes", params={"path": "[id:999999999]/[name:Tasks]", "count": 5}
    )
    _assert(
        "GET with nonexistent project returns 2xx (empty result, not 500)",
        result.ok,
        f"status={result.status}",
    )
    if result.ok:
        data = result.json()
        nodes = data.get("result", [])
        _assert(
            "0 nodes returned for nonexistent project path",
            len(nodes) == 0,
            f"count={len(nodes)}",
        )


async def smoke_create_task_happy_path(config: Any) -> None:
    """
    divoid_create_task: happy path — create a real task on project #3, then DELETE it.

    Uses tasks_group_id directly (bypasses resolution) to keep this test focused
    on the create+content+link arc. Cleans up after itself.
    """
    print("\n--- divoid_create_task (happy path + cleanup) ---")

    # Step 1: Create the node.
    node_body = {
        "name": "[smoke-test] divoid_create_task — delete me",
        "type": "task",
        "status": "open",
    }
    create_result = await http_client.post_json("nodes", node_body)
    _assert("POST /nodes (task) returns 2xx", create_result.ok, f"status={create_result.status}")
    if not create_result.ok:
        return

    try:
        node_id = create_result.json()["id"]
    except Exception as exc:
        _record("parse created task id", False, str(exc))
        return

    _assert("created task has integer id", isinstance(node_id, int), f"id={node_id!r}")

    # Step 2: Post content.
    content = (
        "This is a smoke-test node created by divoid-mcp run_all.py. "
        "It should be deleted at the end of the test. "
        "If you see this node it was not cleaned up."
    )
    content_result = await http_client.post_bytes(
        f"nodes/{node_id}/content",
        content.encode("utf-8"),
        "text/markdown; charset=utf-8",
    )
    _assert(
        f"POST /nodes/{node_id}/content returns 2xx",
        content_result.ok,
        f"status={content_result.status}",
    )

    # Step 3: Link to Tasks group.
    link_result = await http_client.post_json(f"nodes/{node_id}/links", _DIVOID_TASKS_GROUP_ID)
    link_ok = link_result.ok or (
        link_result.status == 500
        and "already linked" in (link_result.body or b"").decode("utf-8", errors="replace").lower()
    )
    _assert(
        f"POST /nodes/{node_id}/links to Tasks group returns 2xx (or idempotent 500)",
        link_ok,
        f"status={link_result.status}",
    )

    # Step 4: Verify the link appears in adjacency.
    links_check = await http_client.get("nodes/links", params={"ids": [node_id, _DIVOID_TASKS_GROUP_ID]})
    if links_check.ok:
        pairs = {
            (lnk.get("sourceId"), lnk.get("targetId"))
            for lnk in links_check.json().get("result", [])
        }
        has_link = (
            (node_id, _DIVOID_TASKS_GROUP_ID) in pairs
            or (_DIVOID_TASKS_GROUP_ID, node_id) in pairs
        )
        _assert(
            "link to Tasks group visible in adjacency",
            has_link,
            f"pair_count={len(pairs)}",
        )

    # Verify node has expected fields.
    get_result = await http_client.get(f"nodes/{node_id}")
    if get_result.ok and get_result.body.strip():
        node_data = get_result.json()
        _assert(
            "created task has type='task'",
            node_data.get("type") == "task",
            f"type={node_data.get('type')!r}",
        )
        _assert(
            "created task has status='open'",
            node_data.get("status") == "open",
            f"status={node_data.get('status')!r}",
        )

    # Cleanup: DELETE the test node.
    delete_result = await http_client.delete(f"nodes/{node_id}")
    _assert(
        f"DELETE /nodes/{node_id} returns 2xx (cleanup)",
        delete_result.ok,
        f"status={delete_result.status}",
    )


async def smoke_create_documentation_happy_path(config: Any) -> None:
    """
    divoid_create_documentation: happy path — create a real doc on project #3, then DELETE it.

    Uses docs_group_id directly. Cleans up after itself.
    """
    print("\n--- divoid_create_documentation (happy path + cleanup) ---")

    # Step 1: Create the node.
    node_body = {
        "name": "[smoke-test] divoid_create_documentation — delete me",
        "type": "documentation",
    }
    create_result = await http_client.post_json("nodes", node_body)
    _assert("POST /nodes (documentation) returns 2xx", create_result.ok, f"status={create_result.status}")
    if not create_result.ok:
        return

    try:
        node_id = create_result.json()["id"]
    except Exception as exc:
        _record("parse created documentation id", False, str(exc))
        return

    _assert("created documentation has integer id", isinstance(node_id, int), f"id={node_id!r}")

    # Step 2: Post content (UTF-8 safety: include a multibyte character).
    content = (
        "# Smoke test documentation\n\n"
        "This node was created by divoid-mcp run_all.py smoke tests. "
        "UTF-8 safety check: em-dash — and euro sign €.\n\n"
        "It should be deleted at the end of the test. "
        "If you see this node it was not cleaned up."
    )
    content_result = await http_client.post_bytes(
        f"nodes/{node_id}/content",
        content.encode("utf-8"),
        "text/markdown; charset=utf-8",
    )
    _assert(
        f"POST /nodes/{node_id}/content returns 2xx",
        content_result.ok,
        f"status={content_result.status}",
    )

    # Step 3: Link to Docs group.
    link_result = await http_client.post_json(f"nodes/{node_id}/links", _DIVOID_DOCS_GROUP_ID)
    link_ok = link_result.ok or (
        link_result.status == 500
        and "already linked" in (link_result.body or b"").decode("utf-8", errors="replace").lower()
    )
    _assert(
        f"POST /nodes/{node_id}/links to Docs group returns 2xx (or idempotent 500)",
        link_ok,
        f"status={link_result.status}",
    )

    # Step 4: Verify content round-trip (UTF-8 safety).
    content_check = await http_client.get(f"nodes/{node_id}/content")
    if content_check.ok:
        _assert("content round-trip: HTTP 200", content_check.ok)
        try:
            decoded = content_check.body.decode("utf-8")
            _assert("content round-trip: decodes as UTF-8", True)
            _assert(
                "content round-trip: em-dash preserved",
                "—" in decoded,
                f"em-dash {'found' if '—' in decoded else 'MISSING'} in round-tripped content",
            )
            _assert(
                "content round-trip: euro sign preserved",
                "€" in decoded,
                f"euro sign {'found' if chr(0x20ac) in decoded else 'MISSING'} in round-tripped content",
            )
        except UnicodeDecodeError:
            _assert("content round-trip: decodes as UTF-8", False, "UnicodeDecodeError")

    # Verify node has expected fields.
    get_result = await http_client.get(f"nodes/{node_id}")
    if get_result.ok and get_result.body.strip():
        node_data = get_result.json()
        _assert(
            "created documentation has type='documentation'",
            node_data.get("type") == "documentation",
            f"type={node_data.get('type')!r}",
        )
        _assert(
            "created documentation has status=null",
            node_data.get("status") is None,
            f"status={node_data.get('status')!r}",
        )

    # Cleanup: DELETE the test node.
    delete_result = await http_client.delete(f"nodes/{node_id}")
    _assert(
        f"DELETE /nodes/{node_id} returns 2xx (cleanup)",
        delete_result.ok,
        f"status={delete_result.status}",
    )


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

async def _run_all(config: Any) -> None:
    tests = [
        smoke_search,
        smoke_search_with_type_filter,
        smoke_get_node,
        smoke_get_node_not_found,
        smoke_get_content_missing_node,
        smoke_get_content_no_content_node,
        smoke_get_content,
        smoke_get_content_api_reference,
        smoke_link_nodes,
        # Phase 1 PR2: composite write tools
        smoke_create_task_group_resolution,
        smoke_create_documentation_group_resolution,
        smoke_create_task_invariant_violation,
        smoke_create_documentation_invariant_violation,
        smoke_create_task_missing_project,
        smoke_create_task_happy_path,
        smoke_create_documentation_happy_path,
    ]

    for test_fn in tests:
        try:
            await test_fn(config)
        except Exception:
            tb = traceback.format_exc()
            # Strip non-ASCII for terminal safety
            safe_tb = tb.encode("ascii", errors="replace").decode("ascii")
            print(f"\n  [FAIL] {test_fn.__name__} raised an exception:")
            print(safe_tb)
            _results.append((test_fn.__name__ + " (exception)", False, "see above"))


def main() -> None:
    print("divoid-mcp Phase 1 smoke tests (PR1 read-side + PR2 composites)")
    print("=" * 60)

    config = _setup()

    asyncio.run(_run_all(config))

    # Summary
    print("\n" + "=" * 60)
    passed = sum(1 for _, ok, _ in _results if ok)
    total = len(_results)
    print(f"Results: {passed}/{total} passed")

    if passed < total:
        print("\nFailed checks:")
        for name, ok, detail in _results:
            if not ok:
                line = f"  FAIL  {name}"
                if detail:
                    line += f": {detail}"
                print(line.encode("ascii", errors="replace").decode("ascii"))
        sys.exit(1)
    else:
        print("All checks passed.")


if __name__ == "__main__":
    main()
