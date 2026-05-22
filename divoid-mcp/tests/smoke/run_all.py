#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
Smoke tests for divoid-mcp tools.

These are live integration scripts -- they call the real DiVoid instance
using the admin key from ~/.claude/secrets/.divoid-online. They are NOT
pytest-based; they print PASS/FAIL and exit non-zero on any failure.

Run from the repo root after `pip install -e .`:

    python tests/smoke/run_all.py

Each test calls the HTTP client functions directly (same code path as the
MCP tools use) and asserts the response has the expected shape.

What "structurally correct" means:
- divoid_search:          returns {"result": [...], "total": int}
- divoid_get_node:        returns {"id": int, "type": str, "name": str, ...}
- divoid_get_content:     returns UTF-8 decodeable bytes with matching content-type
- divoid_link_nodes:      POST /nodes/{id}/links returns 2xx; link visible in adjacency
- divoid_create_task:     creates node + content + Tasks group link atomically
- divoid_create_documentation: creates node + content + Docs group link atomically
- divoid_create_session_log:   creates node + content + Docs group link atomically
- divoid_resolve_user:    GET /nodes/{id}/user -> {user_id: int}
- divoid_send_message:    resolve node->user + POST /messages atomically; cleanup via DELETE
- divoid_list_messages:   GET /messages -> {result, total, continue}

Pinned group ids for smoke-test target (DiVoid project #3):
  Tasks group: #314
  Docs group:  #7
"""

from __future__ import annotations

import asyncio
import subprocess
import sys
import traceback
from typing import Any

from divoid_mcp.config import load_secret
from divoid_mcp import http_client
from divoid_mcp.tools.create_task import _check_invariants as _check_task_invariants
from divoid_mcp.tools.create_documentation import _check_invariants as _check_doc_invariants
from divoid_mcp.tools.create_session_log import _check_invariants as _check_session_log_invariants
from divoid_mcp.tools.create_session_log import _execute as _execute_create_session_log
from divoid_mcp.tools.resolve_user import _execute as _execute_resolve_user
from divoid_mcp.tools.send_message import _check_invariants as _check_send_message_invariants
from divoid_mcp.tools.send_message import _execute as _execute_send_message
from divoid_mcp.tools.list_messages import _execute as _execute_list_messages
from divoid_mcp.tools.list_nodes import _check_invariants as _check_list_invariants
from divoid_mcp.tools.list_nodes import _execute as _execute_list
from divoid_mcp.tools.patch_node import _check_invariants as _check_patch_node_invariants
from divoid_mcp.tools.patch_node import _execute as _execute_patch_node
from divoid_mcp.tools.set_status import _validate_status_for_type
from divoid_mcp.tools.set_status import _execute as _execute_set_status
from divoid_mcp.tools.set_content import _check_invariants as _check_set_content_invariants
from divoid_mcp.tools.set_content import _execute as _execute_set_content
from divoid_mcp.tools.get_links import _check_invariants as _check_get_links_invariants
from divoid_mcp.tools.get_links import _execute as _execute_get_links
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
    divoid_get_node: non-existent node returns 404 (DiVoid #701 fix).

    After the backend fix, GET /api/nodes/{id} returns HTTP 404 (not 200+empty)
    for missing nodes. The 404 is handled directly by errors.map_http_error.
    The content endpoint also returns 404 for missing nodes.
    """
    print("\n--- divoid_get_node (not found) ---")
    # GET /api/nodes/{id} now returns 404 for missing nodes (DiVoid #701 fixed).
    result = await http_client.get("nodes/999999999")
    _assert(
        "GET /nodes/999999999 returns 404",
        result.status == 404,
        f"status={result.status}",
    )

    # The content endpoint also returns 404 for missing nodes.
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

    After the DiVoid #702 backend fix, duplicate POST returns 200 OK (idempotent).
    This test posts the link (may be new or duplicate) and asserts 2xx in both cases.
    """
    print("\n--- divoid_link_nodes ---")

    # Verify both nodes exist first.
    r8 = await http_client.get("nodes/8")
    r9 = await http_client.get("nodes/9")
    if not r8.ok:
        _record("link precondition: node #8 exists", False, "cannot proceed")
        return
    if not r9.ok:
        _record("link precondition: node #9 exists", False, "cannot proceed")
        return

    _record("link precondition: nodes #8 and #9 exist", True)

    # Post the link — DiVoid #702 fix: duplicate POST returns 200 OK (idempotent).
    result = await http_client.post_json("nodes/8/links", 9)
    _assert(
        "POST /nodes/8/links: 2xx (new or duplicate, both idempotent)",
        result.ok,
        f"status={result.status}",
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
    _assert(
        f"POST /nodes/{node_id}/links to Tasks group returns 2xx",
        link_result.ok,
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
    _assert(
        f"POST /nodes/{node_id}/links to Docs group returns 2xx",
        link_result.ok,
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
# Phase 2: divoid_create_session_log
# ---------------------------------------------------------------------------

async def smoke_create_session_log_invariant_violation(config: Any) -> None:
    """
    divoid_create_session_log: whitespace-only content -> content_whitespace_only violation.

    Session-logs have no 'new' escape — content is always required.
    """
    print("\n--- divoid_create_session_log (invariant: content_whitespace_only) ---")

    # Whitespace-only content -> content_whitespace_only
    raised = False
    violation_code = None
    try:
        _check_session_log_invariants(
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
        _check_session_log_invariants(
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
        _check_session_log_invariants(
            name="Smoke test",
            content="Some valid session narrative here.",
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


async def smoke_create_session_log_missing_project(config: Any) -> None:
    """
    divoid_create_session_log: project_id=99999999 -> structured error, not crash.

    The group resolution must return a structured group_not_found message.
    Mirrors the smoke_create_task_missing_project pattern.
    """
    print("\n--- divoid_create_session_log (missing project: group resolution fail) ---")
    result = await http_client.get(
        "nodes", params={"path": "[id:99999999]/[name:Docs]", "count": 5}
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


async def smoke_create_session_log_happy_path(config: Any) -> None:
    """
    divoid_create_session_log: happy path — call _execute() directly to verify the
    tool function itself creates a real session-log, then DELETE it.

    Uses docs_group_id directly (bypasses resolve_group) to keep this test focused
    on the create+content+link arc, mirroring the create_task/create_documentation
    happy-path pattern. Verifies adjacency links and UTF-8 content round-trip.
    Cleans up after itself.

    Load-bearing proof: _execute is the function register() delegates to. Deleting
    register()'s @mcp_server.tool line still leaves _execute callable, but deleting
    _execute itself causes this test to fail at import time (NameError on
    _execute_create_session_log).
    """
    print("\n--- divoid_create_session_log (happy path via _execute + extra_links + cleanup) ---")

    content = (
        "# Smoke test session-log\n\n"
        "This node was created by divoid-mcp run_all.py smoke tests. "
        "UTF-8 safety check: em-dash — and euro sign €.\n\n"
        "Extra links: this session-log is linked to node #8 (API reference) "
        "and node #9 (onboarding) to verify extra_links behaviour.\n\n"
        "It should be deleted at the end of the test. "
        "If you see this node it was not cleaned up."
    )

    # Call the actual tool implementation — not a hand-rolled HTTP sequence.
    result = await _execute_create_session_log(
        name="[smoke-test] divoid_create_session_log -- delete me",
        content=content,
        config=config,
        docs_group_id=_DIVOID_DOCS_GROUP_ID,
        extra_links=[8, 9],
    )

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    node_id = result.get("id")
    _assert("result has integer id", isinstance(node_id, int), f"id={node_id!r}")
    if not isinstance(node_id, int):
        return

    _assert(
        "result type is 'session-log'",
        result.get("type") == "session-log",
        f"type={result.get('type')!r}",
    )
    _assert(
        "result docs_group_id matches expected",
        result.get("docs_group_id") == _DIVOID_DOCS_GROUP_ID,
        f"docs_group_id={result.get('docs_group_id')!r}",
    )
    _assert(
        "result extra_links_attached includes node #8 and #9",
        set(result.get("extra_links_attached", [])) == {8, 9},
        f"extra_links_attached={result.get('extra_links_attached')!r}",
    )
    _assert(
        "result content_length is positive",
        isinstance(result.get("content_length"), int) and result["content_length"] > 0,
        f"content_length={result.get('content_length')!r}",
    )

    # Verify content round-trip (UTF-8 safety).
    content_check = await http_client.get(f"nodes/{node_id}/content")
    if content_check.ok:
        _assert("content round-trip: HTTP 200", content_check.ok)
        try:
            decoded = content_check.body.decode("utf-8")
            _assert("content round-trip: decodes as UTF-8", True)
            _assert(
                "content round-trip: em-dash preserved",
                "—" in decoded,
                f"em-dash {'found' if chr(0x2014) in decoded else 'MISSING'}",
            )
        except UnicodeDecodeError:
            _assert("content round-trip: decodes as UTF-8", False, "UnicodeDecodeError")

    # Verify node type and null status.
    get_result = await http_client.get(f"nodes/{node_id}")
    if get_result.ok and get_result.body.strip():
        node_data = get_result.json()
        _assert(
            "created session-log has type='session-log'",
            node_data.get("type") == "session-log",
            f"type={node_data.get('type')!r}",
        )
        _assert(
            "created session-log has status=null (no lifecycle per #493 §5)",
            node_data.get("status") is None,
            f"status={node_data.get('status')!r}",
        )

    # Verify all expected links appear in adjacency.
    all_expected_links = [_DIVOID_DOCS_GROUP_ID, 8, 9]
    links_check = await http_client.get(
        "nodes/links",
        params={"ids": [node_id] + all_expected_links},
    )
    if links_check.ok:
        pairs = {
            (lnk.get("sourceId"), lnk.get("targetId"))
            for lnk in links_check.json().get("result", [])
        }
        for expected_target in all_expected_links:
            has_link = (node_id, expected_target) in pairs or (expected_target, node_id) in pairs
            _assert(
                f"link session-log -> #{expected_target} visible in adjacency",
                has_link,
                f"pair_count={len(pairs)}",
            )

    # Cleanup: DELETE the test node.
    delete_result = await http_client.delete(f"nodes/{node_id}")
    _assert(
        f"DELETE /nodes/{node_id} returns 2xx (cleanup)",
        delete_result.ok,
        f"status={delete_result.status}",
    )


# ---------------------------------------------------------------------------
# Phase 2 PR2: messaging tools
# ---------------------------------------------------------------------------

# Selene's agent node id and expected user id (pinned).
_SELENE_NODE_ID = 11
_SELENE_USER_ID = 2


async def smoke_resolve_user_happy_path(config: Any) -> None:
    """
    divoid_resolve_user: Selene's agent node (#11) resolves to user_id=2.

    Pins the node->user mapping so we catch any change to Selene's User binding.
    Uses _execute directly — deleting the function breaks the import at the top
    of this file, making this load-bearing in the correct direction.
    """
    print("\n--- divoid_resolve_user (happy path: node #11 -> user_id=2) ---")

    result = await _execute_resolve_user(node_id=_SELENE_NODE_ID, config=config)

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    _assert(
        "user_id is present in result",
        "user_id" in result,
        f"keys={list(result.keys())}",
    )
    _assert(
        f"user_id == {_SELENE_USER_ID} (Selene, pinned)",
        result.get("user_id") == _SELENE_USER_ID,
        f"user_id={result.get('user_id')!r}",
    )


async def smoke_resolve_user_not_found(config: Any) -> None:
    """
    divoid_resolve_user: non-existent node -> structured 404, not crash.

    The API collapses "no such node" and "node has no User binding" into a
    single 404; we verify the tool surfaces it as isError=True with a stable code.
    """
    print("\n--- divoid_resolve_user (not found: node #999999999) ---")

    result = await _execute_resolve_user(node_id=999999999, config=config)

    _assert(
        "_execute returns isError=True for non-existent node",
        result.get("isError", False),
        f"result={result!r}",
    )
    if result.get("isError"):
        content = result.get("content", [])
        # Content is a list of {"type": "text", "text": "..."}.
        text = " ".join(c.get("text", "") for c in content if isinstance(c, dict))
        _assert(
            "error text contains a stable code",
            "node_not_found" in text or "404" in text,
            f"text={text[:200]!r}",
        )


async def smoke_send_message_invariant_violations(config: Any) -> None:
    """
    divoid_send_message: invariant guard rejects malformed calls before HTTP.

    Tests: empty subject, empty body, both recipient fields, neither recipient field.
    All four must raise InvariantViolation with a distinct stable code.
    """
    print("\n--- divoid_send_message (invariant violations) ---")

    # Empty subject.
    raised, code = False, None
    try:
        _check_send_message_invariants(
            subject="",
            body="Some body.",
            recipient_node_id=_SELENE_NODE_ID,
            recipient_user_id=None,
        )
    except InvariantViolation as exc:
        raised, code = True, exc.code
    _assert("empty subject raises InvariantViolation", raised)
    _assert("empty subject code is 'subject_empty'", code == "subject_empty", f"code={code!r}")

    # Empty body.
    raised, code = False, None
    try:
        _check_send_message_invariants(
            subject="[DiVoid] test",
            body="   ",
            recipient_node_id=_SELENE_NODE_ID,
            recipient_user_id=None,
        )
    except InvariantViolation as exc:
        raised, code = True, exc.code
    _assert("whitespace-only body raises InvariantViolation", raised)
    _assert("whitespace body code is 'body_empty'", code == "body_empty", f"code={code!r}")

    # Both recipient fields set.
    raised, code = False, None
    try:
        _check_send_message_invariants(
            subject="[DiVoid] test",
            body="Some body.",
            recipient_node_id=_SELENE_NODE_ID,
            recipient_user_id=_SELENE_USER_ID,
        )
    except InvariantViolation as exc:
        raised, code = True, exc.code
    _assert("both recipient fields raises InvariantViolation", raised)
    _assert(
        "both recipients code is 'mutually_exclusive_recipient'",
        code == "mutually_exclusive_recipient",
        f"code={code!r}",
    )

    # Neither recipient field set.
    raised, code = False, None
    try:
        _check_send_message_invariants(
            subject="[DiVoid] test",
            body="Some body.",
            recipient_node_id=None,
            recipient_user_id=None,
        )
    except InvariantViolation as exc:
        raised, code = True, exc.code
    _assert("neither recipient field raises InvariantViolation", raised)
    _assert("no recipient code is 'no_recipient'", code == "no_recipient", f"code={code!r}")


async def smoke_send_message_happy_path(config: Any) -> None:
    """
    divoid_send_message: self-message to Selene via recipient_node_id, verify inbox, DELETE.

    Uses _execute directly (load-bearing: deleting _execute breaks the import).
    Sends a self-message (Selene -> Selene, the canonical pattern), verifies it
    appears in the inbox via divoid_list_messages, then DELETEs it to clean up.
    """
    print("\n--- divoid_send_message (happy path self-message + verify + cleanup) ---")

    subject = "[DiVoid] smoke-test message — delete me"
    body = (
        "This is a smoke-test message sent by divoid-mcp run_all.py. "
        "It should be deleted at the end of the test. "
        "If you see this message it was not cleaned up."
    )

    result = await _execute_send_message(
        subject=subject,
        body=body,
        config=config,
        recipient_node_id=_SELENE_NODE_ID,
    )

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    message_id = result.get("message_id")
    _assert("result has integer message_id", isinstance(message_id, int), f"id={message_id!r}")
    if not isinstance(message_id, int):
        return

    _assert(
        "result recipient_user_id == 2 (Selene)",
        result.get("recipient_user_id") == _SELENE_USER_ID,
        f"recipient_user_id={result.get('recipient_user_id')!r}",
    )
    _assert(
        "result recipient_node_id == 11 (Selene node, echoed back)",
        result.get("recipient_node_id") == _SELENE_NODE_ID,
        f"recipient_node_id={result.get('recipient_node_id')!r}",
    )
    _assert(
        "result subject matches sent subject",
        result.get("subject") == subject,
        f"subject={result.get('subject')!r}",
    )
    _assert(
        "result has sent_at timestamp",
        bool(result.get("sent_at")),
        f"sent_at={result.get('sent_at')!r}",
    )

    # Verify the message appears in the inbox.
    inbox = await _execute_list_messages(count=50, continue_offset=0, config=config)
    _assert(
        "inbox fetch succeeds",
        not inbox.get("isError", False),
        str(inbox.get("content", "")),
    )
    if not inbox.get("isError", False):
        found_ids = [m.get("id") for m in inbox.get("result", [])]
        _assert(
            f"sent message #{message_id} appears in inbox",
            message_id in found_ids,
            f"inbox_ids={found_ids}",
        )

    # Cleanup: DELETE the test message.
    delete_result = await http_client.delete(f"messages/{message_id}")
    _assert(
        f"DELETE /messages/{message_id} returns 2xx (cleanup)",
        delete_result.ok,
        f"status={delete_result.status}",
    )

    # Verify it is gone from inbox.
    inbox_after = await _execute_list_messages(count=50, continue_offset=0, config=config)
    if not inbox_after.get("isError", False):
        ids_after = [m.get("id") for m in inbox_after.get("result", [])]
        _assert(
            f"deleted message #{message_id} is gone from inbox",
            message_id not in ids_after,
            f"ids_after={ids_after}",
        )


async def smoke_list_messages_happy_path(config: Any) -> None:
    """
    divoid_list_messages: inbox returns {result, total, continue} with correct shape.

    Uses _execute directly (load-bearing: deleting _execute breaks the import).
    The send+cleanup test above has already ensured the inbox is clean of
    smoke-test messages, so we just verify structure and known pre-existing messages.
    """
    print("\n--- divoid_list_messages (happy path: shape + pagination field) ---")

    result = await _execute_list_messages(count=10, continue_offset=0, config=config)

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    _assert("result has 'result' key", "result" in result, f"keys={list(result.keys())}")
    _assert("result has 'total' key", "total" in result, f"keys={list(result.keys())}")
    _assert("result key 'continue' is present", "continue" in result, f"keys={list(result.keys())}")
    _assert(
        "'result' is a list",
        isinstance(result.get("result"), list),
        f"type={type(result.get('result'))!r}",
    )
    _assert(
        "'total' is a non-negative integer",
        isinstance(result.get("total"), int) and result["total"] >= 0,
        f"total={result.get('total')!r}",
    )

    # Each message in the result must have the expected fields.
    messages = result.get("result", [])
    for msg in messages:
        for field in ("id", "authorId", "recipientId", "subject", "body", "createdAt"):
            _assert(
                f"message #{msg.get('id')} has field '{field}'",
                field in msg,
                f"keys={list(msg.keys())}",
            )


# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# Phase 2 PR3: divoid_list
# ---------------------------------------------------------------------------

async def smoke_list_bare(config: Any) -> None:
    """
    divoid_list: bare call returns result array + total, paginatable.

    _execute is imported at the top of this file — deleting it causes an
    ImportError before any test runs (load-bearing proof for the happy path).
    """
    print("\n--- divoid_list (bare list) ---")

    result = await _execute_list(config=config, count=5)

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    _assert("result has 'result' key", "result" in result, f"keys={list(result.keys())}")
    _assert("result has 'total' key", "total" in result, f"keys={list(result.keys())}")
    _assert("'result' is a list", isinstance(result.get("result"), list))
    _assert(
        "'total' is an integer",
        isinstance(result.get("total"), int),
        f"total={result.get('total')!r}",
    )
    _assert(
        "result list has at least 1 node",
        len(result.get("result", [])) >= 1,
        f"count={len(result.get('result', []))}",
    )
    _assert(
        "'continue' key present (may be null)",
        "continue" in result,
        f"keys={list(result.keys())}",
    )


async def smoke_list_type_filter(config: Any) -> None:
    """divoid_list: type=['task'] returns only task nodes (strict, no semantic blend)."""
    print("\n--- divoid_list (type=task filter) ---")

    result = await _execute_list(config=config, type=["task"], count=20)

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    nodes = result.get("result", [])
    _assert("at least one task found", len(nodes) >= 1, f"count={len(nodes)}")

    non_task = [n.get("type") for n in nodes if n.get("type") != "task"]
    _assert(
        "all returned nodes have type=task",
        len(non_task) == 0,
        f"unexpected types: {set(non_task)}" if non_task else f"count={len(nodes)}",
    )


async def smoke_list_linkedto_filter(config: Any) -> None:
    """divoid_list: linkedto=[3] (DiVoid project) returns its direct neighbors."""
    print("\n--- divoid_list (linkedto=[3] DiVoid project) ---")

    result = await _execute_list(config=config, linkedto=[_DIVOID_PROJECT_ID], count=20)

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    nodes = result.get("result", [])
    _assert(
        "at least one neighbor found for project #3",
        len(nodes) >= 1,
        f"count={len(nodes)}",
    )
    # DiVoid project's known direct neighbors include Tasks (#314) and Docs (#7).
    ids_returned = {n.get("id") for n in nodes}
    _assert(
        "Tasks group #314 is in DiVoid's neighbors",
        _DIVOID_TASKS_GROUP_ID in ids_returned,
        f"ids_returned={ids_returned}",
    )
    _assert(
        "Docs group #7 is in DiVoid's neighbors",
        _DIVOID_DOCS_GROUP_ID in ids_returned,
        f"ids_returned={ids_returned}",
    )


async def smoke_list_path_simple(config: Any) -> None:
    """divoid_list: path='[type:project,name:DiVoid]' returns exactly DiVoid project."""
    print("\n--- divoid_list (path simple: project by name) ---")

    result = await _execute_list(config=config, path="[type:project,name:DiVoid]", count=10)

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    nodes = result.get("result", [])
    _assert("exactly 1 node returned", len(nodes) == 1, f"count={len(nodes)}")
    if len(nodes) == 1:
        _assert(
            "returned node id is 3 (DiVoid project, pinned)",
            nodes[0].get("id") == _DIVOID_PROJECT_ID,
            f"id={nodes[0].get('id')!r}",
        )
        _assert(
            "returned node type is 'project'",
            nodes[0].get("type") == "project",
            f"type={nodes[0].get('type')!r}",
        )


async def smoke_list_path_multi_hop(config: Any) -> None:
    """
    divoid_list: path='[id:3]/[name:Tasks]/[type:task,status:open]' returns open tasks.

    Multi-hop: DiVoid (#3) -> Tasks group -> open tasks.
    Sanity-checks that at least one result comes back (DiVoid always has open work).
    """
    print("\n--- divoid_list (path multi-hop: open tasks under DiVoid) ---")

    result = await _execute_list(
        config=config,
        path=f"[id:{_DIVOID_PROJECT_ID}]/[name:Tasks]/[type:task,status:open]",
        count=20,
    )

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    nodes = result.get("result", [])
    _assert(
        "at least one open task found under DiVoid",
        len(nodes) >= 1,
        f"count={len(nodes)}",
    )
    non_task_or_wrong_status = [
        n for n in nodes
        if n.get("type") != "task" or n.get("status") != "open"
    ]
    _assert(
        "all returned nodes are tasks with status=open",
        len(non_task_or_wrong_status) == 0,
        f"unexpected: {[(n.get('type'), n.get('status')) for n in non_task_or_wrong_status]}",
    )


async def smoke_list_path_wildcard(config: Any) -> None:
    """divoid_list: path='[type:project,name:Di%]' returns DiVoid (name LIKE Di%)."""
    print("\n--- divoid_list (path wildcard: name=Di%) ---")

    result = await _execute_list(config=config, path="[type:project,name:Di%]", count=10)

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    nodes = result.get("result", [])
    _assert("at least 1 node returned", len(nodes) >= 1, f"count={len(nodes)}")

    ids_returned = {n.get("id") for n in nodes}
    _assert(
        "DiVoid project (#3) is in the wildcard result",
        _DIVOID_PROJECT_ID in ids_returned,
        f"ids_returned={ids_returned}",
    )
    # All returned nodes must have names starting with 'di' (case-insensitive for safety).
    bad_names = [n.get("name", "") for n in nodes if not n.get("name", "").lower().startswith("di")]
    _assert(
        "all returned names start with 'Di' (LIKE Di%)",
        len(bad_names) == 0,
        f"names not matching: {bad_names}",
    )


async def smoke_list_path_empty_result(config: Any) -> None:
    """divoid_list: path for a nonexistent project returns {result: [], total: 0}."""
    print("\n--- divoid_list (path empty result: nonexistent project) ---")

    result = await _execute_list(
        config=config,
        path="[type:project,name:NoSuchProjectXYZ999]",
        count=10,
    )

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    nodes = result.get("result", [])
    total = result.get("total", -999)
    _assert("result is empty list", nodes == [], f"nodes={nodes}")
    _assert("total is 0", total == 0, f"total={total}")


async def smoke_list_path_and_linkedto_invariant(config: Any) -> None:
    """divoid_list: path + linkedto together -> mutually_exclusive_path_linkedto invariant."""
    print("\n--- divoid_list (invariant: path + linkedto mutually exclusive) ---")

    raised = False
    violation_code = None
    try:
        _check_list_invariants(
            path="[type:project,name:DiVoid]",
            linkedto=[_DIVOID_PROJECT_ID],
            nostatus=False,
            status=None,
            bounds=None,
            sort=None,
            fields=None,
        )
    except InvariantViolation as exc:
        raised = True
        violation_code = exc.code

    _assert("path+linkedto raises InvariantViolation", raised)
    _assert(
        "violation code is 'mutually_exclusive_path_linkedto'",
        violation_code == "mutually_exclusive_path_linkedto",
        f"code={violation_code!r}",
    )


async def smoke_list_nostatus_and_status_invariant(config: Any) -> None:
    """divoid_list: nostatus=True + status=['open'] -> mutually_exclusive_nostatus_status invariant."""
    print("\n--- divoid_list (invariant: nostatus + status mutually exclusive) ---")

    raised = False
    violation_code = None
    try:
        _check_list_invariants(
            path=None,
            linkedto=None,
            nostatus=True,
            status=["open"],
            bounds=None,
            sort=None,
            fields=None,
        )
    except InvariantViolation as exc:
        raised = True
        violation_code = exc.code

    _assert("nostatus+status raises InvariantViolation", raised)
    _assert(
        "violation code is 'mutually_exclusive_nostatus_status'",
        violation_code == "mutually_exclusive_nostatus_status",
        f"code={violation_code!r}",
    )


async def smoke_list_bounds_invalid_length(config: Any) -> None:
    """divoid_list: bounds with 3 elements -> bounds_invalid_length invariant."""
    print("\n--- divoid_list (invariant: bounds invalid length) ---")

    raised = False
    violation_code = None
    try:
        _check_list_invariants(
            path=None,
            linkedto=None,
            nostatus=False,
            status=None,
            bounds=[1.0, 2.0, 3.0],  # only 3 values, should be 4
            sort=None,
            fields=None,
        )
    except InvariantViolation as exc:
        raised = True
        violation_code = exc.code

    _assert("bounds length 3 raises InvariantViolation", raised)
    _assert(
        "violation code is 'bounds_invalid_length'",
        violation_code == "bounds_invalid_length",
        f"code={violation_code!r}",
    )


async def smoke_list_bounds_valid(config: Any) -> None:
    """divoid_list: bounds=[0,0,100000,100000] returns nodes in a very large viewport."""
    print("\n--- divoid_list (bounds valid large viewport) ---")

    result = await _execute_list(
        config=config,
        bounds=[0.0, 0.0, 100000.0, 100000.0],
        count=10,
    )

    _assert(
        "_execute returns no isError for large viewport bounds",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    # A 0..100000 viewport should capture most/all nodes that have canvas positions.
    _assert("result is a list", isinstance(result.get("result"), list))
    _assert("total is non-negative", isinstance(result.get("total"), int) and result["total"] >= 0)


async def smoke_list_sort_name_descending(config: Any) -> None:
    """
    divoid_list: sort=name descending=true returns a valid response with nodes.

    NOTE: This test asserts the API accepts sort+descending without error and returns
    a structurally correct page. It does NOT assert strict lexicographic ordering —
    DiVoid's Postgres instance uses a locale-sensitive collation that does not match
    pure ASCII byte order (e.g. '?' and ':' sort differently than their ASCII values
    suggest). Ordering is the API's responsibility; the tool passes sort+descending
    unchanged and trusts the API.
    """
    print("\n--- divoid_list (sort=name, descending=true) ---")

    result = await _execute_list(
        config=config,
        type=["task"],
        sort="name",
        descending=True,
        count=10,
    )

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    nodes = result.get("result", [])
    _assert(
        "at least 1 task returned with sort=name descending",
        len(nodes) >= 1,
        f"count={len(nodes)}",
    )
    _assert(
        "result is a list of dicts with 'name' field",
        all(isinstance(n, dict) and "name" in n for n in nodes),
        f"sample_keys={[list(n.keys()) for n in nodes[:2]]}",
    )

    # Spot-check: a second call with ascending should return a different first item
    # (assuming > 1 unique name). This indirectly confirms sort direction is honoured.
    result_asc = await _execute_list(
        config=config,
        type=["task"],
        sort="name",
        descending=False,
        count=1,
    )
    result_desc = await _execute_list(
        config=config,
        type=["task"],
        sort="name",
        descending=True,
        count=1,
    )
    if not result_asc.get("isError") and not result_desc.get("isError"):
        asc_first = (result_asc.get("result") or [{}])[0].get("name", "")
        desc_first = (result_desc.get("result") or [{}])[0].get("name", "")
        # With > 1 task in the graph, ascending and descending first items differ.
        _assert(
            "ascending and descending first items differ (confirming sort direction honoured)",
            asc_first != desc_first,
            f"asc_first={asc_first!r} desc_first={desc_first!r}",
        )


async def smoke_list_fields_sparse(config: Any) -> None:
    """divoid_list: fields=['id','name'] returns only those two fields per node."""
    print("\n--- divoid_list (fields sparse projection: id+name only) ---")

    result = await _execute_list(
        config=config,
        count=5,
        fields=["id", "name"],
    )

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    nodes = result.get("result", [])
    _assert("at least one node returned", len(nodes) >= 1, f"count={len(nodes)}")

    for node in nodes:
        _assert(
            f"node #{node.get('id')} has 'id' field",
            "id" in node,
            f"keys={list(node.keys())}",
        )
        _assert(
            f"node #{node.get('id')} has 'name' field",
            "name" in node,
            f"keys={list(node.keys())}",
        )
        # type, status, contentType should NOT be in the response.
        for excluded in ("type", "status", "contentType"):
            _assert(
                f"node #{node.get('id')} does NOT have '{excluded}' (excluded by fields)",
                excluded not in node,
                f"keys={list(node.keys())}",
            )


async def smoke_list_pagination(config: Any) -> None:
    """
    divoid_list: count=2 + continue cursor paginates without duplicates.

    Fetches 3 pages of 2 nodes each, collects up to 6 ids, and asserts
    no id appears twice across pages.
    """
    print("\n--- divoid_list (pagination: count=2, walk 3 pages) ---")

    seen_ids: list[int] = []
    cursor = None

    for page in range(3):
        kwargs: dict[str, Any] = {"count": 2}
        if cursor is not None:
            kwargs["continue_cursor"] = cursor

        result = await _execute_list(config=config, **kwargs)

        _assert(
            f"page {page + 1}: no isError",
            not result.get("isError", False),
            str(result.get("content", "")),
        )
        if result.get("isError"):
            break

        page_nodes = result.get("result", [])
        if not page_nodes:
            # Ran out of nodes before 3 pages — that is fine, just stop.
            break

        page_ids = [n.get("id") for n in page_nodes]
        _assert(
            f"page {page + 1}: at most 2 nodes returned",
            len(page_nodes) <= 2,
            f"count={len(page_nodes)}",
        )

        for nid in page_ids:
            _assert(
                f"id #{nid} (page {page + 1}) not seen on a prior page",
                nid not in seen_ids,
                f"seen_ids={seen_ids}",
            )
            seen_ids.append(nid)

        cursor = result.get("continue")
        if cursor is None:
            break

    _assert("at least 2 unique ids collected across pages", len(seen_ids) >= 2, f"ids={seen_ids}")


async def smoke_list_nototal(config: Any) -> None:
    """
    divoid_list: nototal=True is accepted and returns a valid response.

    NOTE: DiVoid node #8 (API spec) states nototal=True causes total=-1 (COUNT skipped).
    In practice the current prod backend appears to still compute and return the real
    total. The tool passes nototal faithfully as a query parameter; the test asserts
    only that the call succeeds and total is an integer — not its specific value —
    since the API behaviour may differ from the spec or change. If the API is updated
    to honour nototal, total=-1 would be the correct assertion here.
    """
    print("\n--- divoid_list (nototal=True) ---")

    result = await _execute_list(config=config, nototal=True, count=5)

    _assert(
        "_execute returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    total = result.get("total")
    _assert(
        "total is an integer (nototal flag accepted, value -1 or real count)",
        isinstance(total, int),
        f"total={total!r}",
    )
    _assert("result is still a list", isinstance(result.get("result"), list))


async def smoke_list_sort_invalid_invariant(config: Any) -> None:
    """divoid_list: sort='foobar' -> sort_invalid_field invariant rejection."""
    print("\n--- divoid_list (invariant: sort invalid field) ---")

    raised = False
    violation_code = None
    try:
        _check_list_invariants(
            path=None,
            linkedto=None,
            nostatus=False,
            status=None,
            bounds=None,
            sort="foobar",
            fields=None,
        )
    except InvariantViolation as exc:
        raised = True
        violation_code = exc.code

    _assert("invalid sort raises InvariantViolation", raised)
    _assert(
        "violation code is 'sort_invalid_field'",
        violation_code == "sort_invalid_field",
        f"code={violation_code!r}",
    )


### -----------------------------------------------------------------------
### Phase 2 polish: patch_node, set_status, set_content, get_links
### -----------------------------------------------------------------------

async def smoke_patch_node_invariant_no_fields(config: Any) -> None:
    """divoid_patch_node: no_fields_to_patch invariant fires before any HTTP call."""
    print("\n--- divoid_patch_node (invariant: no_fields_to_patch) ---")

    raised = False
    violation_code = None
    try:
        _check_patch_node_invariants(name=None, status=None, x=None, y=None)
    except InvariantViolation as exc:
        raised = True
        violation_code = exc.code

    _assert("no fields raises InvariantViolation", raised)
    _assert(
        "violation code is 'no_fields_to_patch'",
        violation_code == "no_fields_to_patch",
        f"code={violation_code!r}",
    )


async def smoke_patch_node_not_found(config: Any) -> None:
    """divoid_patch_node: 404 on a non-existent node id."""
    print("\n--- divoid_patch_node (404 error mapping) ---")

    result = await _execute_patch_node(
        id=999999999,
        config=config,
        name="should not exist",
    )

    _assert("result is error", result.get("isError", False))
    content = result.get("content", [])
    if content:
        error_text = content[0].get("text", "")
        _assert(
            "error code is node_not_found",
            error_text.startswith("node_not_found"),
            f"text={error_text[:80]!r}",
        )


async def smoke_patch_node_happy_path(config: Any) -> None:
    """
    divoid_patch_node: happy path — create a task, patch its name, verify, delete.

    Load-bearing proof: _execute is imported by name at the top of run_all.py.
    Deleting _execute from patch_node.py causes an ImportError that aborts the runner.
    """
    print("\n--- divoid_patch_node (happy path + cleanup) ---")

    # Step 1: Create a scratch task node.
    node_body = {"name": "[smoke] patch_node-original — delete me", "type": "task", "status": "open"}
    create_result = await http_client.post_json("nodes", node_body)
    _assert("POST /nodes (task) returns 2xx", create_result.ok, f"status={create_result.status}")
    if not create_result.ok:
        return

    try:
        node_id = create_result.json()["id"]
    except Exception as exc:
        _record("parse created node id", False, str(exc))
        return

    _assert("created node has integer id", isinstance(node_id, int), f"id={node_id!r}")

    try:
        # Step 2: Patch the name via _execute.
        new_name = "[smoke] patch_node-patched — delete me"
        patch_result = await _execute_patch_node(
            id=node_id,
            config=config,
            name=new_name,
        )

        _assert(
            "patch_node returns no isError",
            not patch_result.get("isError", False),
            str(patch_result.get("content", "")),
        )
        if patch_result.get("isError"):
            return

        _assert(
            "patched node has updated name",
            patch_result.get("name") == new_name,
            f"name={patch_result.get('name')!r}",
        )
        _assert("patched node has id", patch_result.get("id") == node_id, f"id={patch_result.get('id')!r}")

        # Step 3: Patch two fields at once (status + x).
        patch2_result = await _execute_patch_node(
            id=node_id,
            config=config,
            status="in-progress",
            x=100.0,
        )
        _assert(
            "patch two fields at once returns no isError",
            not patch2_result.get("isError", False),
            str(patch2_result.get("content", "")),
        )
        if not patch2_result.get("isError"):
            _assert(
                "patched node has updated status",
                patch2_result.get("status") == "in-progress",
                f"status={patch2_result.get('status')!r}",
            )

    finally:
        # Cleanup: always delete.
        delete_result = await http_client.delete(f"nodes/{node_id}")
        _assert(
            f"DELETE /nodes/{node_id} returns 2xx (cleanup)",
            delete_result.ok,
            f"status={delete_result.status}",
        )


### -----------------------------------------------------------------------

async def smoke_set_status_invariant_task_wrong_status(config: Any) -> None:
    """divoid_set_status: 'fixed' is not valid for task -> status_not_in_task_lifecycle."""
    print("\n--- divoid_set_status (invariant: task status_not_in_task_lifecycle) ---")

    raised = False
    violation_code = None
    try:
        _validate_status_for_type("task", "fixed")
    except InvariantViolation as exc:
        raised = True
        violation_code = exc.code

    _assert("'fixed' on task raises InvariantViolation", raised)
    _assert(
        "violation code is 'status_not_in_task_lifecycle'",
        violation_code == "status_not_in_task_lifecycle",
        f"code={violation_code!r}",
    )


async def smoke_set_status_invariant_documentation(config: Any) -> None:
    """divoid_set_status: any status on 'documentation' -> status_not_supported_for_type."""
    print("\n--- divoid_set_status (invariant: documentation status_not_supported_for_type) ---")

    raised = False
    violation_code = None
    try:
        _validate_status_for_type("documentation", "open")
    except InvariantViolation as exc:
        raised = True
        violation_code = exc.code

    _assert("status on documentation raises InvariantViolation", raised)
    _assert(
        "violation code is 'status_not_supported_for_type'",
        violation_code == "status_not_supported_for_type",
        f"code={violation_code!r}",
    )


async def smoke_set_status_invariant_bug_wrong_status(config: Any) -> None:
    """divoid_set_status: 'closed' is not valid for bug -> status_not_in_bug_lifecycle."""
    print("\n--- divoid_set_status (invariant: bug status_not_in_bug_lifecycle) ---")

    raised = False
    violation_code = None
    try:
        _validate_status_for_type("bug", "closed")
    except InvariantViolation as exc:
        raised = True
        violation_code = exc.code

    _assert("'closed' on bug raises InvariantViolation", raised)
    _assert(
        "violation code is 'status_not_in_bug_lifecycle'",
        violation_code == "status_not_in_bug_lifecycle",
        f"code={violation_code!r}",
    )


async def smoke_set_status_not_found(config: Any) -> None:
    """divoid_set_status: 404 on non-existent node id surfaced as node_not_found."""
    print("\n--- divoid_set_status (404 error mapping) ---")

    result = await _execute_set_status(id=999999999, status="open", config=config)

    _assert("result is error", result.get("isError", False))
    content = result.get("content", [])
    if content:
        error_text = content[0].get("text", "")
        _assert(
            "error code is node_not_found",
            error_text.startswith("node_not_found"),
            f"text={error_text[:80]!r}",
        )


async def smoke_set_status_happy_path(config: Any) -> None:
    """
    divoid_set_status: happy path — create task, set status to in-progress, then closed, delete.

    Load-bearing proof: _execute is imported by name at the top of run_all.py.
    Deleting _execute from set_status.py causes an ImportError that aborts the runner.
    """
    print("\n--- divoid_set_status (happy path + cleanup) ---")

    # Step 1: Create a scratch task.
    node_body = {"name": "[smoke] set_status — delete me", "type": "task", "status": "open"}
    create_result = await http_client.post_json("nodes", node_body)
    _assert("POST /nodes (task) returns 2xx", create_result.ok, f"status={create_result.status}")
    if not create_result.ok:
        return

    try:
        node_id = create_result.json()["id"]
    except Exception as exc:
        _record("parse created node id", False, str(exc))
        return

    try:
        # Step 2: Set status to in-progress — valid task lifecycle transition.
        result = await _execute_set_status(id=node_id, status="in-progress", config=config)
        _assert(
            "set_status(in-progress) returns no isError",
            not result.get("isError", False),
            str(result.get("content", "")),
        )
        if not result.get("isError"):
            _assert(
                "returned node has status=in-progress",
                result.get("status") == "in-progress",
                f"status={result.get('status')!r}",
            )

        # Step 3: Try setting 'fixed' (bug-only status) — should fire invariant.
        inv_result = await _execute_set_status(id=node_id, status="fixed", config=config)
        _assert(
            "set_status('fixed') on task returns isError",
            inv_result.get("isError", False),
        )
        if inv_result.get("isError"):
            error_text = inv_result.get("content", [{}])[0].get("text", "")
            _assert(
                "error code is status_not_in_task_lifecycle",
                error_text.startswith("status_not_in_task_lifecycle"),
                f"text={error_text[:80]!r}",
            )

        # Step 4: Set status to closed — valid.
        result2 = await _execute_set_status(id=node_id, status="closed", config=config)
        _assert(
            "set_status(closed) returns no isError",
            not result2.get("isError", False),
            str(result2.get("content", "")),
        )

    finally:
        # Cleanup.
        delete_result = await http_client.delete(f"nodes/{node_id}")
        _assert(
            f"DELETE /nodes/{node_id} returns 2xx (cleanup)",
            delete_result.ok,
            f"status={delete_result.status}",
        )


### -----------------------------------------------------------------------

async def smoke_set_content_invariant_empty(config: Any) -> None:
    """divoid_set_content: content_empty invariant fires for whitespace-only content."""
    print("\n--- divoid_set_content (invariant: content_empty) ---")

    raised = False
    violation_code = None
    try:
        _check_set_content_invariants("   ")
    except InvariantViolation as exc:
        raised = True
        violation_code = exc.code

    _assert("whitespace-only content raises InvariantViolation", raised)
    _assert(
        "violation code is 'content_empty'",
        violation_code == "content_empty",
        f"code={violation_code!r}",
    )

    # Also test empty string.
    raised2 = False
    try:
        _check_set_content_invariants("")
    except InvariantViolation:
        raised2 = True
    _assert("empty string raises InvariantViolation", raised2)


async def smoke_set_content_not_found(config: Any) -> None:
    """divoid_set_content: 404 on a non-existent node id."""
    print("\n--- divoid_set_content (404 error mapping) ---")

    result = await _execute_set_content(
        id=999999999,
        content="# Test content\n\nThis should 404.",
        config=config,
    )

    _assert("result is error", result.get("isError", False))
    content = result.get("content", [])
    if content:
        error_text = content[0].get("text", "")
        _assert(
            "error code is node_not_found",
            error_text.startswith("node_not_found"),
            f"text={error_text[:80]!r}",
        )


async def smoke_set_content_happy_path(config: Any) -> None:
    """
    divoid_set_content: happy path — create a node, set content, verify length, delete.

    Also verifies UTF-8 roundtrip: multibyte characters survive the post.

    Load-bearing proof: _execute is imported by name at the top of run_all.py.
    Deleting _execute from set_content.py causes an ImportError that aborts the runner.
    """
    print("\n--- divoid_set_content (happy path + cleanup) ---")

    # Step 1: Create a bare documentation node (no content).
    node_body = {"name": "[smoke] set_content — delete me", "type": "documentation"}
    create_result = await http_client.post_json("nodes", node_body)
    _assert("POST /nodes (doc) returns 2xx", create_result.ok, f"status={create_result.status}")
    if not create_result.ok:
        return

    try:
        node_id = create_result.json()["id"]
    except Exception as exc:
        _record("parse created node id", False, str(exc))
        return

    try:
        # Step 2: Set content via _execute — includes multibyte chars for UTF-8 proof.
        content_body = (
            "# Smoke test content\n\n"
            "UTF-8 multibyte: café naïve résumé 中文\n\n"
            "This node was created by the divoid-mcp smoke test and should be deleted."
        )
        result = await _execute_set_content(id=node_id, content=content_body, config=config)

        _assert(
            "set_content returns no isError",
            not result.get("isError", False),
            str(result.get("content", "")),
        )
        if result.get("isError"):
            return

        _assert("result has id", result.get("id") == node_id, f"id={result.get('id')!r}")
        _assert(
            "result has content_length > 0",
            isinstance(result.get("content_length"), int) and result["content_length"] > 0,
            f"content_length={result.get('content_length')!r}",
        )
        _assert(
            "result content_type is markdown",
            "text/markdown" in result.get("content_type", ""),
            f"content_type={result.get('content_type')!r}",
        )

        # Step 3: Verify content round-trips correctly by re-fetching.
        content_check = await http_client.get(f"nodes/{node_id}/content")
        _assert(
            "GET /nodes/{id}/content returns 2xx after set",
            content_check.ok,
            f"status={content_check.status}",
        )
        if content_check.ok:
            fetched = content_check.body.decode("utf-8")
            _assert(
                "UTF-8 multibyte chars round-tripped correctly",
                "café" in fetched and "中文" in fetched,
                f"fetched_snippet={fetched[:80]!r}",
            )

    finally:
        # Cleanup.
        delete_result = await http_client.delete(f"nodes/{node_id}")
        _assert(
            f"DELETE /nodes/{node_id} returns 2xx (cleanup)",
            delete_result.ok,
            f"status={delete_result.status}",
        )


### -----------------------------------------------------------------------

async def smoke_get_links_invariant_empty_ids(config: Any) -> None:
    """divoid_get_links: ids_empty invariant fires for empty ids list."""
    print("\n--- divoid_get_links (invariant: ids_empty) ---")

    raised = False
    violation_code = None
    try:
        _check_get_links_invariants([])
    except InvariantViolation as exc:
        raised = True
        violation_code = exc.code

    _assert("empty ids raises InvariantViolation", raised)
    _assert(
        "violation code is 'ids_empty'",
        violation_code == "ids_empty",
        f"code={violation_code!r}",
    )


async def smoke_get_links_single_node(config: Any) -> None:
    """divoid_get_links: fetch links for node #3 (DiVoid project) — should have edges."""
    print("\n--- divoid_get_links (single known node) ---")

    result = await _execute_get_links(ids=[3], config=config)

    _assert(
        "get_links returns no isError",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    links = result.get("result", [])
    _assert("result is a list", isinstance(links, list))
    _assert("at least one link for node #3", len(links) > 0, f"count={len(links)}")
    if links:
        first = links[0]
        _assert("link has source_id", "source_id" in first, f"keys={list(first.keys())}")
        _assert("link has target_id", "target_id" in first, f"keys={list(first.keys())}")
        _assert("source_id is int", isinstance(first["source_id"], int), f"source_id={first['source_id']!r}")
        _assert("target_id is int", isinstance(first["target_id"], int), f"target_id={first['target_id']!r}")
    _assert("total is int", isinstance(result.get("total"), int), f"total={result.get('total')!r}")


async def smoke_get_links_multiple_nodes(config: Any) -> None:
    """divoid_get_links: fetch links for multiple nodes — Tasks group + Docs group."""
    print("\n--- divoid_get_links (multiple nodes: Tasks + Docs groups) ---")

    result = await _execute_get_links(
        ids=[_DIVOID_TASKS_GROUP_ID, _DIVOID_DOCS_GROUP_ID],
        config=config,
    )

    _assert(
        "get_links returns no isError for multiple ids",
        not result.get("isError", False),
        str(result.get("content", "")),
    )
    if result.get("isError"):
        return

    links = result.get("result", [])
    _assert("result is a list", isinstance(links, list))
    _assert(
        "at least 2 links returned for Tasks + Docs groups",
        len(links) >= 2,
        f"count={len(links)}",
    )

    # At least one link should touch one of our target ids.
    involved_ids = {lnk["source_id"] for lnk in links} | {lnk["target_id"] for lnk in links}
    _assert(
        "at least one of ids [314, 7] appears in link endpoints",
        bool(involved_ids & {_DIVOID_TASKS_GROUP_ID, _DIVOID_DOCS_GROUP_ID}),
        f"involved_ids_sample={list(involved_ids)[:5]}",
    )


async def smoke_get_links_happy_path(config: Any) -> None:
    """
    divoid_get_links: happy path with cleanup — create two nodes + link, verify adjacency,
    then delete.

    Load-bearing proof: _execute is imported by name at the top of run_all.py.
    Deleting _execute from get_links.py causes an ImportError that aborts the runner.
    """
    print("\n--- divoid_get_links (happy path: create link + verify adjacency + cleanup) ---")

    # Create two scratch nodes.
    node_a_body = {"name": "[smoke] get_links-A — delete me", "type": "documentation"}
    node_b_body = {"name": "[smoke] get_links-B — delete me", "type": "documentation"}
    ra = await http_client.post_json("nodes", node_a_body)
    rb = await http_client.post_json("nodes", node_b_body)

    _assert("create node A returns 2xx", ra.ok, f"status={ra.status}")
    _assert("create node B returns 2xx", rb.ok, f"status={rb.status}")
    if not (ra.ok and rb.ok):
        return

    try:
        id_a = ra.json()["id"]
        id_b = rb.json()["id"]
    except Exception as exc:
        _record("parse node ids", False, str(exc))
        return

    try:
        # Link A → B.
        link_result = await http_client.post_json(f"nodes/{id_a}/links", id_b)
        _assert("link A→B returns 2xx", link_result.ok, f"status={link_result.status}")
        if not link_result.ok:
            return

        # Verify via _execute_get_links.
        links_result = await _execute_get_links(ids=[id_a, id_b], config=config)
        _assert(
            "get_links returns no isError",
            not links_result.get("isError", False),
            str(links_result.get("content", "")),
        )
        if links_result.get("isError"):
            return

        pairs = {
            (lnk["source_id"], lnk["target_id"])
            for lnk in links_result.get("result", [])
        }
        has_link = (id_a, id_b) in pairs or (id_b, id_a) in pairs
        _assert(
            "link between A and B visible in get_links result",
            has_link,
            f"pairs={list(pairs)[:5]}",
        )

    finally:
        # Cleanup: delete both nodes.
        del_a = await http_client.delete(f"nodes/{id_a}")
        del_b = await http_client.delete(f"nodes/{id_b}")
        _assert(f"DELETE node A ({id_a}) returns 2xx", del_a.ok, f"status={del_a.status}")
        _assert(f"DELETE node B ({id_b}) returns 2xx", del_b.ok, f"status={del_b.status}")


### -----------------------------------------------------------------------
### Server bootstrap smoke test (the one that catches FastMCP API mismatches)
### -----------------------------------------------------------------------

async def smoke_server_bootstrap(config: Any) -> None:
    """
    Server bootstrap: spawn `python -m divoid_mcp` as a subprocess and verify
    clean startup.

    This is the test that would have caught the `version=__version__` TypeError
    (FastMCP.__init__() got an unexpected keyword argument 'version') before
    it reached production.  The subprocess reads its own secrets file the same
    way as a real deployment — no config passed from the parent.

    Load-bearing proof (DiVoid #275): reverting server.py to include
    `version=__version__` in the FastMCP constructor call causes this test
    to FAIL with the traceback assertion.  The substitution proof was run
    during development of this PR and confirmed before restore.

    What we assert:
    1. The process started (Popen succeeds; no immediate crash on import).
    2. stderr contains the two expected startup log lines:
         "divoid-mcp <version> starting."
         "config-loaded" (or "divoid-mcp ready")
    3. stderr contains NO Python traceback (no "Traceback (most recent call last)").
    """
    print("\n--- server bootstrap (subprocess spawn + stderr log check) ---")

    from divoid_mcp.version import __version__ as _version

    proc = subprocess.Popen(
        [sys.executable, "-m", "divoid_mcp"],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )

    try:
        # Give the server up to 5 seconds to log its startup lines and then exit
        # (it will block on stdin forever unless we close it — stdin=DEVNULL
        # causes the JSON-RPC reader to get EOF and exit cleanly).
        try:
            _, stderr_bytes = proc.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            _, stderr_bytes = proc.communicate()

        stderr_text = stderr_bytes.decode("utf-8", errors="replace")
    finally:
        # Always ensure the process is gone.
        if proc.poll() is None:
            proc.kill()
            proc.wait()

    # Assertion 1: startup log line present.
    expected_starting = f"divoid-mcp {_version} starting."
    _assert(
        "stderr contains startup log line",
        expected_starting in stderr_text,
        f"expected={expected_starting!r} stderr_snippet={stderr_text[:300]!r}",
    )

    # Assertion 2: ready line present (server got past FastMCP construction).
    _assert(
        "stderr contains 'ready' log line (FastMCP constructed without error)",
        "divoid-mcp ready" in stderr_text,
        f"stderr_snippet={stderr_text[:300]!r}",
    )

    # Assertion 3: NO Python traceback.
    has_traceback = "Traceback (most recent call last)" in stderr_text
    _assert(
        "stderr contains no Python traceback",
        not has_traceback,
        f"traceback found — stderr_snippet={stderr_text[:500]!r}" if has_traceback else "",
    )


### -----------------------------------------------------------------------

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
        # Phase 2: create_session_log composite
        smoke_create_session_log_invariant_violation,
        smoke_create_session_log_missing_project,
        smoke_create_session_log_happy_path,
        # Phase 2 PR2: messaging tools
        smoke_resolve_user_happy_path,
        smoke_resolve_user_not_found,
        smoke_send_message_invariant_violations,
        smoke_send_message_happy_path,
        smoke_list_messages_happy_path,
        # Phase 2 PR3: divoid_list
        smoke_list_bare,
        smoke_list_type_filter,
        smoke_list_linkedto_filter,
        smoke_list_path_simple,
        smoke_list_path_multi_hop,
        smoke_list_path_wildcard,
        smoke_list_path_empty_result,
        smoke_list_path_and_linkedto_invariant,
        smoke_list_nostatus_and_status_invariant,
        smoke_list_bounds_invalid_length,
        smoke_list_bounds_valid,
        smoke_list_sort_name_descending,
        smoke_list_fields_sparse,
        smoke_list_pagination,
        smoke_list_nototal,
        smoke_list_sort_invalid_invariant,
        # Phase 2 polish: patch_node, set_status, set_content, get_links primitives
        smoke_patch_node_invariant_no_fields,
        smoke_patch_node_not_found,
        smoke_patch_node_happy_path,
        smoke_set_status_invariant_task_wrong_status,
        smoke_set_status_invariant_documentation,
        smoke_set_status_invariant_bug_wrong_status,
        smoke_set_status_not_found,
        smoke_set_status_happy_path,
        smoke_set_content_invariant_empty,
        smoke_set_content_not_found,
        smoke_set_content_happy_path,
        smoke_get_links_invariant_empty_ids,
        smoke_get_links_single_node,
        smoke_get_links_multiple_nodes,
        smoke_get_links_happy_path,
        # Bootstrap: subprocess spawn verifies FastMCP API compat at startup
        smoke_server_bootstrap,
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
    print("divoid-mcp smoke tests (Phase 1 + Phase 2: read-side + composites + messaging + list + primitives + bootstrap)")
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
