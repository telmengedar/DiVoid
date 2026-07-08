"""
divoid_edit_content -- partial content editor over PATCH /api/nodes/{id}/content.

Sends an ordered list of ergonomic edits as a single atomic PATCH to the backend
endpoint introduced in DiVoid PR #159 (design: #6284). The backend applies all edits
against the content as it was read (original-frame addressing) and either commits the
full batch or rejects it entirely (all-or-nothing).

Wire format: PATCH /api/nodes/{id}/content, body = JSON array of
  {"unit": "line"|"char", "start": int, "length": int, "value": string}
The backend is 0-based and half-open [start, start+length). Each edit here translates
from human-readable 1-based inclusive ranges to that wire shape internally.

Supported ergonomic verbs:
  replace_lines       -- replace a range of lines (1-based inclusive)
  replace_chars       -- replace a range of Unicode code points (1-based inclusive)
  insert_before_line  -- insert text immediately before a line (1-based)
  delete_lines        -- delete a range of lines (1-based inclusive)
  append              -- append text after the last character (triggers one GET pre-read)

The "append" verb requires knowing the current length of the content in code points.
If any append op is present, the tool fetches the node content once before the PATCH
to compute that count. If no append is present, no GET is issued — pure arithmetic.

Dependency: PATCH /api/nodes/{id}/content is live only after PR #159 merges + deploys.
Unit tests mock the HTTP layer; the live smoke test tolerates a 404/405 gracefully.

Architecture reference: DiVoid task #6285, design #6284.
API wire contract: ContentEdit.cs on branch feat/partial-content-editing.
"""

from __future__ import annotations

import logging
from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import InvariantViolation, make_error_content, map_http_error, map_unreachable

logger = logging.getLogger(__name__)

_TOOL_DESCRIPTION = """\
Apply one or more partial edits to a DiVoid node's text content in a single atomic \
request. Edits use 1-based inclusive line/char ranges (human-readable), translated \
to the backend's 0-based half-open format internally. All edits are addressed against \
the content as it was at call time and applied atomically — if any edit fails, none \
are applied.

Supported verbs (each edit dict must have an "op" key):

  replace_lines     Replace lines start_line through end_line (1-based inclusive) with \
value. Example: {"op": "replace_lines", "start_line": 3, "end_line": 5, "value": "new\\n"}

  replace_chars     Replace Unicode code points start through end (1-based inclusive) \
with value. Example: {"op": "replace_chars", "start": 10, "end": 20, "value": "new text"}

  insert_before_line  Insert value immediately before line N (1-based). value should \
end with \\n to remain a complete line. \
Example: {"op": "insert_before_line", "line": 3, "value": "inserted line\\n"}

  delete_lines      Delete lines start_line through end_line (1-based inclusive). \
Example: {"op": "delete_lines", "start_line": 3, "end_line": 5}

  append            Append value after the last character of the content. Fetches the \
node content once to determine current length; all appends in a batch share that one \
pre-read. Example: {"op": "append", "value": "\\n## New Section\\n"}

Returns the updated NodeDetails on success. 400 from the server means bad addressing \
(overlap, out-of-bounds, non-text content, or empty batch); 404 means missing node or \
no content set.\
"""

# All verbs accepted by _check_invariants and _execute.
_KNOWN_OPS = frozenset(["replace_lines", "replace_chars", "insert_before_line", "delete_lines", "append"])


def _check_invariants(edits: list[dict[str, Any]]) -> None:
    """
    Guard structural invariants before any HTTP call.

    Raises InvariantViolation with a stable code on the first violation found.
    Only structural constraints are checked here (positivity, ordering, known op).
    Content values are never inspected — the backend is the authority on those.
    """
    if not edits:
        raise InvariantViolation(
            "empty_edits",
            "edits must contain at least one edit. An empty batch is always rejected by the backend.",
        )

    for i, edit in enumerate(edits):
        op = edit.get("op")
        if op not in _KNOWN_OPS:
            raise InvariantViolation(
                "unknown_op",
                f"edits[{i}].op={op!r} is not a recognized verb. "
                f"Valid ops: {', '.join(sorted(_KNOWN_OPS))}.",
            )

        if op == "replace_lines":
            sl = edit.get("start_line")
            el = edit.get("end_line")
            if sl is None or el is None:
                raise InvariantViolation(
                    "missing_field",
                    f"edits[{i}] (replace_lines) requires both start_line and end_line.",
                )
            if sl < 1 or el < 1:
                raise InvariantViolation(
                    "non_positive_index",
                    f"edits[{i}] (replace_lines): start_line and end_line must be >= 1, "
                    f"got start_line={sl}, end_line={el}.",
                )
            if el < sl:
                raise InvariantViolation(
                    "end_before_start",
                    f"edits[{i}] (replace_lines): end_line={el} must be >= start_line={sl}.",
                )

        elif op == "replace_chars":
            s = edit.get("start")
            e = edit.get("end")
            if s is None or e is None:
                raise InvariantViolation(
                    "missing_field",
                    f"edits[{i}] (replace_chars) requires both start and end.",
                )
            if s < 1 or e < 1:
                raise InvariantViolation(
                    "non_positive_index",
                    f"edits[{i}] (replace_chars): start and end must be >= 1, "
                    f"got start={s}, end={e}.",
                )
            if e < s:
                raise InvariantViolation(
                    "end_before_start",
                    f"edits[{i}] (replace_chars): end={e} must be >= start={s}.",
                )

        elif op == "insert_before_line":
            line = edit.get("line")
            if line is None:
                raise InvariantViolation(
                    "missing_field",
                    f"edits[{i}] (insert_before_line) requires line.",
                )
            if line < 1:
                raise InvariantViolation(
                    "non_positive_index",
                    f"edits[{i}] (insert_before_line): line must be >= 1, got line={line}.",
                )

        elif op == "delete_lines":
            sl = edit.get("start_line")
            el = edit.get("end_line")
            if sl is None or el is None:
                raise InvariantViolation(
                    "missing_field",
                    f"edits[{i}] (delete_lines) requires both start_line and end_line.",
                )
            if sl < 1 or el < 1:
                raise InvariantViolation(
                    "non_positive_index",
                    f"edits[{i}] (delete_lines): start_line and end_line must be >= 1, "
                    f"got start_line={sl}, end_line={el}.",
                )
            if el < sl:
                raise InvariantViolation(
                    "end_before_start",
                    f"edits[{i}] (delete_lines): end_line={el} must be >= start_line={sl}.",
                )

        # "append" has no positional fields — no structural constraint beyond known op.


async def _execute(
    id: int,
    edits: list[dict[str, Any]],
    config: "DivoidConfig",
) -> dict[str, Any]:
    """
    Core implementation of divoid_edit_content.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.

    Callers must run _check_invariants() before calling this function.
    """
    # If any append op is present, fetch the node content once to learn the
    # current code-point count. All appends in the batch use this single count
    # (original-frame: all edits share the content snapshot at call time).
    char_count: int | None = None
    if any(e.get("op") == "append" for e in edits):
        logger.info("divoid_edit_content id=%d: append detected — fetching content for char_count", id)
        try:
            get_result = await http_client.get(f"nodes/{id}/content")
        except http_client.DiVoidUnreachable as exc:
            code, msg = map_unreachable(exc, config.api_key, f"GET content pre-read for append on node #{id}")
            logger.warning("divoid_edit_content id=%d append pre-read unreachable: %s", id, code)
            return {"isError": True, "content": make_error_content(code, msg)}

        if not get_result.ok:
            code, msg = map_http_error(
                get_result.status, get_result.body, config.api_key,
                f"GET content pre-read for append on node #{id}",
            )
            logger.warning("divoid_edit_content id=%d append pre-read err=%s status=%d", id, code, get_result.status)
            return {"isError": True, "content": make_error_content(code, msg)}

        # Python str len() counts Unicode code points — exactly the unit the backend uses.
        char_count = len(get_result.body.decode("utf-8", errors="replace"))
        logger.info("divoid_edit_content id=%d char_count=%d for append", id, char_count)

    # Translate each ergonomic edit to the backend wire shape.
    # Conversion: 1-based inclusive [a, b] → 0-based half-open [a-1, b-a+1).
    backend_edits: list[dict[str, Any]] = []
    for edit in edits:
        op = edit["op"]

        if op == "replace_lines":
            sl, el = edit["start_line"], edit["end_line"]
            backend_edits.append({
                "unit": "line",
                "start": sl - 1,
                "length": el - sl + 1,
                "value": edit.get("value", ""),
            })

        elif op == "replace_chars":
            s, e = edit["start"], edit["end"]
            backend_edits.append({
                "unit": "char",
                "start": s - 1,
                "length": e - s + 1,
                "value": edit.get("value", ""),
            })

        elif op == "insert_before_line":
            line = edit["line"]
            backend_edits.append({
                "unit": "line",
                "start": line - 1,
                "length": 0,
                "value": edit.get("value", ""),
            })

        elif op == "delete_lines":
            sl, el = edit["start_line"], edit["end_line"]
            backend_edits.append({
                "unit": "line",
                "start": sl - 1,
                "length": el - sl + 1,
                "value": "",
            })

        elif op == "append":
            # char_count is guaranteed non-None here (invariant: append implies pre-read above).
            backend_edits.append({
                "unit": "char",
                "start": char_count,
                "length": 0,
                "value": edit.get("value", ""),
            })

    logger.info("divoid_edit_content id=%d sending %d backend edit(s)", id, len(backend_edits))

    try:
        result = await http_client.patch_json(f"nodes/{id}/content", backend_edits)
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, f"PATCH content on node #{id}")
        logger.warning("divoid_edit_content id=%d unreachable: %s", id, code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not result.ok:
        code, msg = map_http_error(
            result.status, result.body, config.api_key,
            f"PATCH content on node #{id}",
        )
        logger.info("divoid_edit_content id=%d err=%s status=%d", id, code, result.status)
        return {"isError": True, "content": make_error_content(code, msg)}

    try:
        node_data = result.json()
    except Exception as exc:
        return {
            "isError": True,
            "content": make_error_content(
                "divoid_bad_request",
                f"PATCH content node #{id}: Could not parse response: {exc}",
            ),
        }

    logger.info("divoid_edit_content id=%d ok", id)
    return node_data


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_edit_content(
        id: int,
        edits: list[dict[str, Any]],
    ) -> dict[str, Any]:
        """
        Apply partial edits to a DiVoid node's text content in one atomic PATCH.

        Args:
            id: The node id whose content to edit (required, positive integer).
            edits: Ordered list of edit dicts. Each dict must have an "op" key
                   selecting the verb, plus verb-specific fields. All line numbers
                   and character positions are 1-based and inclusive.

                   replace_lines: replace a line range with new text.
                     Required fields: start_line (int >= 1), end_line (int >= start_line),
                                      value (str, the replacement text).
                     Example: {"op": "replace_lines", "start_line": 3, "end_line": 5,
                               "value": "replacement\\n"}

                   replace_chars: replace a Unicode code-point range with new text.
                     Required fields: start (int >= 1), end (int >= start), value (str).
                     Example: {"op": "replace_chars", "start": 10, "end": 20,
                               "value": "new text"}

                   insert_before_line: insert text before a given line (no deletion).
                     Required fields: line (int >= 1), value (str).
                     Example: {"op": "insert_before_line", "line": 3,
                               "value": "inserted line\\n"}

                   delete_lines: delete a range of lines entirely.
                     Required fields: start_line (int >= 1), end_line (int >= start_line).
                     Example: {"op": "delete_lines", "start_line": 3, "end_line": 5}

                   append: append text after the very last character of the content.
                     Required fields: value (str).
                     Example: {"op": "append", "value": "\\n## New Section\\n"}
                     Note: triggers one GET pre-read to determine current content length;
                     all appends in a single call share that one pre-read.

                   Structural invariants checked before any HTTP call:
                   - edits must be non-empty
                   - op must be one of the five verbs above
                   - line/char positions must be >= 1
                   - end must be >= start for range verbs
        """
        try:
            _check_invariants(edits)
        except InvariantViolation as exc:
            logger.debug("divoid_edit_content invariant violation: %s", exc.code)
            return {"isError": True, "content": make_error_content(exc.code, exc.message)}

        return await _execute(id=id, edits=edits, config=config)
