"""
Unit tests for divoid_edit_content: translation correctness + invariant guard.

These tests mock the HTTP transport layer (via respx) and assert that:
  1. Each ergonomic verb translates to the correct backend wire shape
     (1-based inclusive → 0-based half-open for lines and chars).
  2. Multiple edits are sent in a single PATCH in the original order.
  3. The "append" verb fetches node content first (one GET), uses len(content)
     as Start (code-point count), and then issues the PATCH.
  4. When no "append" verb is present, no GET is issued — pure arithmetic.
  5. The invariant guard rejects structurally invalid inputs before any HTTP call:
     empty edits, unknown op, non-positive line/char numbers, end < start.

No network calls and no DiVoid credentials are required — respx intercepts every
outbound request. These tests are NOT integration tests; they pin the translation
logic so a bug in _execute cannot slip through the smoke suite undetected.

Architecture reference: DiVoid task #6285, design #6284.
"""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.edit_content import register as register_edit_content

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_CONTENT_URL_TEMPLATE = f"{_DUMMY_BASE}/nodes/{{id}}/content"
_NODE_URL_TEMPLATE = f"{_DUMMY_BASE}/nodes/{{id}}"

# Shared node id used across tests that don't need multiple nodes.
_NODE_ID = 42


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="module")
def server() -> FastMCP:
    """
    Module-scoped FastMCP server with only divoid_edit_content registered.

    Uses dummy config — no real credentials, no network. http_client is
    initialised with the dummy base URL so respx can intercept all calls.
    """
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-edit-content-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_edit_content(mcp_server)

    return mcp_server


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    """Call divoid_edit_content with the given args and return the raw dict."""
    result = await server._tool_manager.call_tool("divoid_edit_content", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


# Minimal success response the mock backend returns.
_OK_NODE = {"id": _NODE_ID, "type": "documentation", "name": "Test node"}


# ---------------------------------------------------------------------------
# replace_lines translation: 1-based inclusive → 0-based half-open
#
# Human "lines 3–5" (3 lines) → start=2, length=3.
# Substitution probe: if the sl-1 or el-sl+1 arithmetic is wrong,
# the PATCH body will have incorrect start/length and the assertion fails.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_replace_lines_translates_to_0_based_half_open(server: FastMCP) -> None:
    """replace_lines start_line=3, end_line=5 → backend start=2, length=3 (Unit=line).

    Substitution probe: change sl-1 to sl in _execute — start becomes 3 (wrong); test fails.
    """
    captured: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_lines", "start_line": 3, "end_line": 5, "value": "new\n"}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured) == 1, "Expected exactly one PATCH call"
    edits = captured[0]
    assert len(edits) == 1, f"Expected 1 backend edit, got {len(edits)}"
    e = edits[0]
    assert e["unit"] == "line", f"Expected unit='line', got: {e['unit']!r}"
    assert e["start"] == 2, (
        f"Expected start=2 (3-1), got: {e['start']!r}. "
        "Substitution probe: start_line - 1 must convert 1-based to 0-based."
    )
    assert e["length"] == 3, (
        f"Expected length=3 (5-3+1), got: {e['length']!r}. "
        "Substitution probe: end_line - start_line + 1 must span the inclusive range."
    )
    assert e["value"] == "new\n", f"Expected value='new\\n', got: {e['value']!r}"


@pytest.mark.asyncio
async def test_replace_lines_single_line_has_length_one(server: FastMCP) -> None:
    """replace_lines start_line=7, end_line=7 → backend start=6, length=1.

    Substitution probe: change length to end_line - start_line → length=0 (insert, wrong); test fails.
    """
    captured: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_lines", "start_line": 7, "end_line": 7, "value": "x\n"}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    e = captured[0][0]
    assert e["start"] == 6, f"Expected start=6 (7-1), got: {e['start']!r}"
    assert e["length"] == 1, (
        f"Expected length=1 for a single-line replace, got: {e['length']!r}. "
        "Substitution probe: length must be el-sl+1, not el-sl."
    )


# ---------------------------------------------------------------------------
# replace_chars translation: 1-based inclusive → 0-based half-open
#
# Human "chars 5–10" (6 code points) → start=4, length=6.
# Substitution probe: if s-1 or e-s+1 is wrong, start/length will be off.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_replace_chars_translates_to_0_based_half_open(server: FastMCP) -> None:
    """replace_chars start=5, end=10 → backend start=4, length=6 (Unit=char).

    Substitution probe: change s-1 to s in _execute — start becomes 5 (wrong); test fails.
    """
    captured: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_chars", "start": 5, "end": 10, "value": "hello"}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured) == 1
    e = captured[0][0]
    assert e["unit"] == "char", f"Expected unit='char', got: {e['unit']!r}"
    assert e["start"] == 4, (
        f"Expected start=4 (5-1), got: {e['start']!r}. "
        "Substitution probe: start - 1 must convert from 1-based to 0-based."
    )
    assert e["length"] == 6, (
        f"Expected length=6 (10-5+1), got: {e['length']!r}. "
        "Substitution probe: end - start + 1 must span the inclusive range."
    )
    assert e["value"] == "hello", f"Expected value='hello', got: {e['value']!r}"


# ---------------------------------------------------------------------------
# insert_before_line: Length must be 0 (pure insertion, no deletion)
#
# Substitution probe: if length is anything other than 0, it would also
# delete existing content — the test detects this.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_insert_before_line_has_zero_length(server: FastMCP) -> None:
    """insert_before_line line=3 → backend start=2, length=0, Unit=line.

    Substitution probe: set length=1 in _execute for insert_before_line — test fails
    because length != 0 (insert would also delete a line).
    """
    captured: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "insert_before_line", "line": 3, "value": "inserted\n"}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    e = captured[0][0]
    assert e["unit"] == "line", f"Expected unit='line', got: {e['unit']!r}"
    assert e["start"] == 2, f"Expected start=2 (3-1), got: {e['start']!r}"
    assert e["length"] == 0, (
        f"Expected length=0 (pure insertion — no deletion), got: {e['length']!r}. "
        "Substitution probe: insert_before_line must set length=0, not any positive value."
    )
    assert e["value"] == "inserted\n", f"Expected value='inserted\\n', got: {e['value']!r}"


# ---------------------------------------------------------------------------
# delete_lines: Value must be empty string (no replacement)
#
# Substitution probe: if value passes through a caller-supplied value,
# the delete becomes a replace — the test detects this.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_delete_lines_has_empty_value_and_correct_range(server: FastMCP) -> None:
    """delete_lines start_line=2, end_line=4 → backend start=1, length=3, value=''.

    Substitution probe: pass value through from the edit dict in _execute —
    value would be None/missing instead of '' and the assertion fails.
    """
    captured: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "delete_lines", "start_line": 2, "end_line": 4}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    e = captured[0][0]
    assert e["unit"] == "line", f"Expected unit='line', got: {e['unit']!r}"
    assert e["start"] == 1, f"Expected start=1 (2-1), got: {e['start']!r}"
    assert e["length"] == 3, f"Expected length=3 (4-2+1), got: {e['length']!r}"
    assert e["value"] == "", (
        f"Expected value='' (delete, not replace), got: {e['value']!r}. "
        "Substitution probe: delete_lines must always set value='', ignoring any caller value."
    )


# ---------------------------------------------------------------------------
# append: GET pre-read → Start = char count; PATCH uses that count
#
# Substitution probe: skip the GET call — char_count stays None and the PATCH
# either crashes or sends None as start; both fail the assertion.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_append_fetches_char_count_and_sets_start(server: FastMCP) -> None:
    """append: GET /nodes/{id}/content first → len(body) as Start for PATCH.

    Content is 'hello' (5 ASCII code points) → char_count=5 → PATCH start=5.

    Substitution probe: remove the GET pre-read block in _execute — char_count remains
    None and the backend edit has start=None (or crashes); the assertion on start=5 fails.
    """
    content_bytes = "hello".encode("utf-8")
    captured_patches: list[Any] = []
    captured_gets: list[httpx.Request] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_get(req: httpx.Request) -> httpx.Response:
            captured_gets.append(req)
            return httpx.Response(200, content=content_bytes, headers={"content-type": "text/plain"})

        def capture_patch(req: httpx.Request) -> httpx.Response:
            captured_patches.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_get)
        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_patch)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "append", "value": " world"}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_gets) == 1, (
        f"Expected exactly 1 GET pre-read for append, got {len(captured_gets)}. "
        "Substitution probe: removing the GET pre-read block causes this to be 0."
    )
    assert len(captured_patches) == 1, "Expected exactly 1 PATCH call"
    e = captured_patches[0][0]
    assert e["unit"] == "char", f"Expected unit='char', got: {e['unit']!r}"
    assert e["start"] == 5, (
        f"Expected start=5 (len('hello')), got: {e['start']!r}. "
        "Substitution probe: the GET response body must be decoded and len() used as Start."
    )
    assert e["length"] == 0, f"Expected length=0 (insertion at end), got: {e['length']!r}"
    assert e["value"] == " world", f"Expected value=' world', got: {e['value']!r}"


@pytest.mark.asyncio
async def test_append_unicode_char_count_uses_code_points(server: FastMCP) -> None:
    """append: content 'hello 🌍' has 7 code points (not 10 bytes) → start=7.

    Python str len() counts code points, matching the backend's Char addressing.

    Substitution probe: use len(body_bytes) instead of len(decoded) — start would be
    10 (UTF-8 byte count), not 7; the assertion on start=7 fails.
    """
    # 'hello 🌍' = 7 code points, 10 UTF-8 bytes (earth emoji = 4 bytes)
    content_bytes = "hello \U0001f30d".encode("utf-8")
    assert len(content_bytes) == 10  # bytes
    assert len("hello \U0001f30d") == 7  # code points

    captured_patches: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(
            return_value=httpx.Response(200, content=content_bytes, headers={"content-type": "text/plain"})
        )

        def capture_patch(req: httpx.Request) -> httpx.Response:
            captured_patches.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_patch)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "append", "value": "!"}],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    e = captured_patches[0][0]
    assert e["start"] == 7, (
        f"Expected start=7 (code-point count of 'hello 🌍'), got: {e['start']!r}. "
        "Substitution probe: using byte count (10) instead of code-point count (7) "
        "would give start=10; backend would reject the edit as out-of-bounds."
    )


# ---------------------------------------------------------------------------
# No append → no GET (zero extra round-trips)
#
# Substitution probe: add an unconditional GET pre-read in _execute — this test
# fails because GET is issued even when no append verb is present.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_no_append_no_content_fetch(server: FastMCP) -> None:
    """Two replace_lines edits (no append) → only ONE HTTP call (PATCH), no GET.

    Substitution probe: add an unconditional content GET at the start of _execute —
    the GET URL would be called and this test fails (captured_gets would be non-empty).
    """
    captured_gets: list[httpx.Request] = []
    captured_patches: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_get(req: httpx.Request) -> httpx.Response:
            captured_gets.append(req)
            return httpx.Response(200, content=b"irrelevant")

        def capture_patch(req: httpx.Request) -> httpx.Response:
            captured_patches.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_get)
        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_patch)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [
                {"op": "replace_lines", "start_line": 1, "end_line": 1, "value": "A\n"},
                {"op": "replace_lines", "start_line": 3, "end_line": 3, "value": "B\n"},
            ],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_gets) == 0, (
        f"Expected 0 GET calls (no append verb present), got {len(captured_gets)}. "
        "Substitution probe: an unconditional pre-read would trigger a GET here."
    )
    assert len(captured_patches) == 1, f"Expected exactly 1 PATCH call, got {len(captured_patches)}"


# ---------------------------------------------------------------------------
# Multi-edit: array sent in one PATCH, original order preserved
#
# Substitution probe: loop and send one PATCH per edit — len(patches) > 1; test fails.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_multi_edit_sent_as_single_patch_in_order(server: FastMCP) -> None:
    """Three edits of mixed verbs → one PATCH with three items in original order.

    Substitution probe: split edits into individual PATCH calls in _execute —
    len(captured_patches) would be 3, not 1; the assertion fails.
    """
    captured_patches: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured_patches.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [
                {"op": "replace_lines", "start_line": 1, "end_line": 1, "value": "X\n"},
                {"op": "insert_before_line", "line": 5, "value": "inserted\n"},
                {"op": "delete_lines", "start_line": 8, "end_line": 10},
            ],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_patches) == 1, (
        f"Expected exactly 1 PATCH call (atomic batch), got {len(captured_patches)}. "
        "Substitution probe: splitting into per-edit PATCHes would give len=3."
    )
    edits = captured_patches[0]
    assert len(edits) == 3, f"Expected 3 edits in the PATCH body, got {len(edits)}"

    # Order must be preserved: replace_lines first, insert_before_line second, delete_lines third.
    assert edits[0]["unit"] == "line" and edits[0]["length"] == 1, (
        f"First edit must be replace_lines (length=1), got: {edits[0]!r}"
    )
    assert edits[1]["unit"] == "line" and edits[1]["length"] == 0, (
        f"Second edit must be insert_before_line (length=0), got: {edits[1]!r}"
    )
    assert edits[2]["unit"] == "line" and edits[2]["value"] == "", (
        f"Third edit must be delete_lines (value=''), got: {edits[2]!r}"
    )


@pytest.mark.asyncio
async def test_append_single_pre_read_shared_by_multiple_appends(server: FastMCP) -> None:
    """Multiple appends in one call → exactly one GET pre-read, one PATCH with both appends.

    All appends in a batch share the same original-frame char count (one pre-read).

    Substitution probe: issue one GET per append in _execute — len(captured_gets)=2; fails.
    """
    content_bytes = b"abc"  # 3 code points
    captured_gets: list[httpx.Request] = []
    captured_patches: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture_get(req: httpx.Request) -> httpx.Response:
            captured_gets.append(req)
            return httpx.Response(200, content=content_bytes, headers={"content-type": "text/plain"})

        def capture_patch(req: httpx.Request) -> httpx.Response:
            captured_patches.append(json.loads(req.content))
            return httpx.Response(200, json=_OK_NODE)

        mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_get)
        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=capture_patch)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [
                {"op": "append", "value": "X"},
                {"op": "append", "value": "Y"},
            ],
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert len(captured_gets) == 1, (
        f"Expected exactly 1 GET pre-read (shared by both appends), got {len(captured_gets)}. "
        "Substitution probe: one GET per append would give len=2."
    )
    assert len(captured_patches) == 1, "Expected exactly 1 PATCH (atomic batch)"
    edits = captured_patches[0]
    assert len(edits) == 2, f"Expected 2 edits in the PATCH body, got {len(edits)}"
    # Both appends use the same char_count=3 from the single pre-read.
    assert edits[0]["start"] == 3, f"First append must use start=3 (len('abc')), got: {edits[0]['start']!r}"
    assert edits[1]["start"] == 3, f"Second append must use same original-frame start=3, got: {edits[1]['start']!r}"


# ---------------------------------------------------------------------------
# Invariant guard: structural violations rejected BEFORE any HTTP call
#
# Substitution probe for each case: remove the matching check in _check_invariants
# — the HTTP mock would be called (or the wrong violation code returned) and the test fails.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_empty_edits_rejected_before_http(server: FastMCP) -> None:
    """Empty edits list → invariant guard returns isError with 'empty_edits', no HTTP call.

    Substitution probe: remove the `if not edits` check from _check_invariants —
    the PATCH would be called with an empty array and the assertion on isError fails.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect_http(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect_http)
        mock.get(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect_http)

        result = await _call(server, {"id": _NODE_ID, "edits": []})

    assert result.get("isError") is True, f"Expected isError=True for empty edits, got: {result}"
    assert not http_called, (
        "HTTP must NOT be called when the invariant guard fires. "
        "Substitution probe: removing the empty-edits check lets a PATCH through."
    )
    text = result["content"][0]["text"]
    assert "empty_edits" in text, (
        f"Expected 'empty_edits' code in error text, got: {text!r}. "
        "Substitution probe: wrong code would appear if the wrong guard branch is triggered."
    )


@pytest.mark.asyncio
async def test_unknown_op_rejected_before_http(server: FastMCP) -> None:
    """Unknown op → invariant guard returns isError with 'unknown_op', no HTTP call.

    Substitution probe: remove the `op not in _KNOWN_OPS` check — the edit would
    be silently skipped or cause a KeyError in _execute; both differ from the expected behavior.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json=_OK_NODE)

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "splice_telepathically", "start_line": 1, "end_line": 1, "value": "x"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for unknown op, got: {result}"
    assert not http_called, "HTTP must NOT be called when op is unknown"
    text = result["content"][0]["text"]
    assert "unknown_op" in text, f"Expected 'unknown_op' code, got: {text!r}"


@pytest.mark.asyncio
async def test_non_positive_line_rejected_before_http(server: FastMCP) -> None:
    """start_line=0 → invariant guard returns isError with 'non_positive_index', no HTTP.

    Line numbers are 1-based; 0 is invalid and the guard must catch it before the PATCH.

    Substitution probe: remove the `sl < 1` check in _check_invariants — the PATCH
    would be sent with start=-1 (0-1), which the backend would reject as 400, but the
    guard is supposed to fire first (this test specifically checks it fires before HTTP).
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(400, json={"code": "error"})

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_lines", "start_line": 0, "end_line": 3, "value": "x"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for line=0, got: {result}"
    assert not http_called, "HTTP must NOT be called — guard must fire before PATCH"
    text = result["content"][0]["text"]
    assert "non_positive_index" in text, f"Expected 'non_positive_index' code, got: {text!r}"


@pytest.mark.asyncio
async def test_end_before_start_rejected_before_http(server: FastMCP) -> None:
    """end_line < start_line → invariant guard returns isError with 'end_before_start'.

    Substitution probe: remove the `el < sl` check — a PATCH with length=-1 would
    be sent, which the backend would reject as 400, but the guard must fire first.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(400, json={"code": "error"})

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_lines", "start_line": 5, "end_line": 3, "value": "x"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for end<start, got: {result}"
    assert not http_called, "HTTP must NOT be called — guard must fire before PATCH"
    text = result["content"][0]["text"]
    assert "end_before_start" in text, f"Expected 'end_before_start' code, got: {text!r}"


@pytest.mark.asyncio
async def test_end_before_start_replace_chars_rejected(server: FastMCP) -> None:
    """replace_chars end < start → 'end_before_start' before HTTP.

    Substitution probe: remove the `e < s` check in the replace_chars branch —
    the PATCH would fire with a negative length.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(400, json={"code": "error"})

        mock.patch(_CONTENT_URL_TEMPLATE.format(id=_NODE_ID)).mock(side_effect=detect)

        result = await _call(server, {
            "id": _NODE_ID,
            "edits": [{"op": "replace_chars", "start": 10, "end": 5, "value": "x"}],
        })

    assert result.get("isError") is True, f"Expected isError=True for chars end<start, got: {result}"
    assert not http_called, "HTTP must NOT be called when end < start"
    text = result["content"][0]["text"]
    assert "end_before_start" in text, f"Expected 'end_before_start' code, got: {text!r}"
