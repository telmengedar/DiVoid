"""
Unit tests for divoid_set_content: inline-vs-file mutual exclusion, byte-identical
file upload, and the file-read guards (missing/unreadable/empty).

These tests mock the HTTP transport layer (via respx) and use tmp_path for real
file I/O. They assert:
  1. The inline `content` path keeps working exactly as before (regression pin).
  2. `path` reads a file's bytes and posts them byte-identical, including bytes
     that broke prior real incidents: an unescaped '|' in a markdown table, a
     CRLF line ending, a multi-byte UTF-8 character, and a lone '\\r'.
  3. `content` and `path` are mutually exclusive: both set or neither set is an
     InvariantViolation, before any HTTP call.
  4. A missing file, an unreadable file, and a file that reads as zero bytes are
     each rejected with a distinct code before any HTTP call.

No network calls and no DiVoid credentials are required.

Architecture reference: DiVoid #8523 §Defect 2, #7895 Finding 1.
"""

from __future__ import annotations

from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.set_content import register as register_set_content

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_NODE_ID = 7
_CONTENT_URL = f"{_DUMMY_BASE}/nodes/{_NODE_ID}/content"

_TRAP_BYTES = (
    "| col `a` | value |\r\n"
    "| --- | --- |\r\n"
    "| pipe | a | b |\r\n"
    "unicode: über \U0001f30d\r\n"
).encode("utf-8") + b"\rlone-cr-no-lf"


@pytest.fixture(scope="module")
def server() -> FastMCP:
    """Module-scoped FastMCP server with only divoid_set_content registered."""
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-set-content-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_set_content(mcp_server)

    return mcp_server


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool("divoid_set_content", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def _error_code(result: dict[str, Any]) -> str:
    text = result.get("content", [{}])[0].get("text", "")
    return text.split(":", 1)[0]


# ---------------------------------------------------------------------------
# Dual: the pre-existing inline `content` path keeps working unchanged
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_inline_content_still_posts_utf8_bytes(server: FastMCP) -> None:
    """content='hello' -> POST body is b'hello', unchanged from the pre-`path` behaviour.

    Substitution probe: route inline content through the file-read branch by
    mistake -- open('hello', 'rb') raises FileNotFoundError and this fails.
    """
    captured: list[bytes] = []

    with respx.mock(assert_all_called=True) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(req.content)
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=capture)

        result = await _call(server, {"id": _NODE_ID, "content": "hello"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert captured[0] == b"hello", f"Expected byte-identical inline post, got: {captured[0]!r}"
    assert result.get("content_length") == 5


@pytest.mark.asyncio
async def test_inline_content_empty_rejected_before_http(server: FastMCP) -> None:
    """content='   ' (whitespace-only) -> content_empty, no HTTP call (pre-existing guard).

    Substitution probe: remove the content.strip() check -- the POST would be
    sent with whitespace-only bytes and this test's no-HTTP assertion fails.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=detect)

        result = await _call(server, {"id": _NODE_ID, "content": "   "})

    assert result.get("isError") is True
    assert not http_called, "HTTP must NOT be called when content is whitespace-only."
    assert _error_code(result) == "content_empty"


# ---------------------------------------------------------------------------
# The test that matters most: file bytes upload byte-identical, including the
# exact characters that broke the reported incidents.
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_path_uploads_trap_bytes_byte_identical(server: FastMCP, tmp_path: Any) -> None:
    """A file with an unescaped '|', CRLF, a multi-byte char, and a lone CR
    uploads byte-for-byte identical -- no decode/re-encode step touches it.

    Substitution probe: decode the file as UTF-8 and re-encode before posting --
    the lone trailing '\\r' with no following byte still round-trips under a
    naive decode/encode, so the CRLF-normalizing failure mode this guards
    against is a decode step that maps '\\r\\n' -> '\\n' (universal-newlines
    text mode); reading via `open(path, 'r')` instead of `'rb'` would trigger
    exactly that and this assertion would fail.
    """
    target = tmp_path / "trap.md"
    target.write_bytes(_TRAP_BYTES)

    captured: list[bytes] = []

    with respx.mock(assert_all_called=True) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(req.content)
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=capture)

        result = await _call(server, {"id": _NODE_ID, "path": str(target)})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert captured[0] == _TRAP_BYTES, (
        f"Uploaded bytes differ from the source file -- a decode/re-encode step occurred.\n"
        f"  expected: {_TRAP_BYTES!r}\n"
        f"  actual:   {captured[0]!r}"
    )
    assert result.get("content_length") == len(_TRAP_BYTES)


@pytest.mark.asyncio
async def test_path_binary_bytes_byte_identical(server: FastMCP, tmp_path: Any) -> None:
    """A non-UTF-8 binary file (PNG magic + invalid UTF-8 byte) uploads byte-identical.

    Substitution probe: encode/decode as UTF-8 anywhere in the path -- 0xFF is
    not valid UTF-8 and would raise UnicodeDecodeError or get replaced/dropped.
    """
    payload = b"\x89PNG\r\n\x1a\n\xff\xd8\xff\xe0\x00\x10"
    target = tmp_path / "image.bin"
    target.write_bytes(payload)

    captured: list[bytes] = []

    with respx.mock(assert_all_called=True) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(req.content)
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=capture)

        result = await _call(
            server,
            {"id": _NODE_ID, "path": str(target), "content_type": "application/octet-stream"},
        )

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert captured[0] == payload, f"Expected byte-identical binary upload, got: {captured[0]!r}"


@pytest.mark.asyncio
async def test_path_uses_default_content_type_when_not_overridden(
    server: FastMCP, tmp_path: Any
) -> None:
    """path with no content_type override -> the same default as the inline path.

    Substitution probe: infer content_type from the file extension instead of
    using the default -- a '.md' file would still match here, so also assert
    the returned content_type field equals the explicit default constant.
    """
    target = tmp_path / "doc.md"
    target.write_bytes(b"# heading\n")

    with respx.mock(assert_all_called=True) as mock:
        mock.post(_CONTENT_URL).mock(return_value=httpx.Response(200, json={"id": _NODE_ID}))

        result = await _call(server, {"id": _NODE_ID, "path": str(target)})

    assert result.get("content_type") == "text/markdown; charset=utf-8"


# ---------------------------------------------------------------------------
# Mutual exclusion: exactly one of content / path
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_both_content_and_path_rejected_before_http(server: FastMCP, tmp_path: Any) -> None:
    """content AND path both given -> content_path_conflict, no HTTP call.

    Substitution probe: remove the `content is not None and path is not None`
    check -- one of the two sources would silently win and the HTTP mock
    would be called; the no-HTTP assertion fails.
    """
    target = tmp_path / "both.md"
    target.write_bytes(b"file body")
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=detect)

        result = await _call(server, {"id": _NODE_ID, "content": "inline", "path": str(target)})

    assert result.get("isError") is True
    assert not http_called, "HTTP must NOT be called when content and path conflict."
    assert _error_code(result) == "content_path_conflict"


@pytest.mark.asyncio
async def test_neither_content_nor_path_rejected_before_http(server: FastMCP) -> None:
    """Neither content nor path given -> content_path_required, no HTTP call.

    Substitution probe: remove the `content is None and path is None` check --
    _execute would run with content=None and crash on content.encode(), or
    silently post no body; either way this test's isError assertion catches it.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=detect)

        result = await _call(server, {"id": _NODE_ID})

    assert result.get("isError") is True
    assert not http_called, "HTTP must NOT be called when neither content nor path is given."
    assert _error_code(result) == "content_path_required"


@pytest.mark.asyncio
async def test_empty_path_rejected_before_http(server: FastMCP) -> None:
    """path='' -> path_empty, no HTTP call.

    Substitution probe: remove the path.strip() check -- open('', 'rb') raises
    FileNotFoundError, which the file-read branch maps to 'file_not_found'
    instead of 'path_empty'; the error-code assertion distinguishes the two.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=detect)

        result = await _call(server, {"id": _NODE_ID, "path": ""})

    assert result.get("isError") is True
    assert not http_called, "HTTP must NOT be called when path is empty."
    assert _error_code(result) == "path_empty"


# ---------------------------------------------------------------------------
# File-read guards: missing / unreadable / empty, each before any HTTP call
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_missing_file_rejected_before_http(server: FastMCP, tmp_path: Any) -> None:
    """path points at a file that does not exist -> file_not_found, no HTTP call.

    Substitution probe: catch OSError broadly before FileNotFoundError-specific
    handling and return 'file_read_failed' instead -- the code assertion here
    distinguishes the two branches.
    """
    target = tmp_path / "does_not_exist.md"
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=detect)

        result = await _call(server, {"id": _NODE_ID, "path": str(target)})

    assert result.get("isError") is True
    assert not http_called, "HTTP must NOT be called when the file does not exist."
    assert _error_code(result) == "file_not_found"


@pytest.mark.asyncio
async def test_unreadable_file_rejected_before_http(server: FastMCP, tmp_path: Any) -> None:
    """path points at a directory (unreadable as a file) -> file_read_failed, no HTTP call.

    A directory is used as the unreadable-file case because it is reproducible
    without depending on platform-specific permission semantics (chmod is
    unreliable for denying the owner read access on Windows).

    Substitution probe: catch only FileNotFoundError and let IsADirectoryError
    propagate uncaught -- the tool would raise instead of returning isError,
    and this test would fail with an exception instead of the expected dict.
    """
    target = tmp_path / "a_directory"
    target.mkdir()
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=detect)

        result = await _call(server, {"id": _NODE_ID, "path": str(target)})

    assert result.get("isError") is True
    assert not http_called, "HTTP must NOT be called when the file cannot be read."
    assert _error_code(result) == "file_read_failed"


@pytest.mark.asyncio
async def test_empty_file_rejected_before_http(server: FastMCP, tmp_path: Any) -> None:
    """path points at a zero-byte file -> file_empty, no HTTP call.

    This is the #7878/#7872 regression guard: a zero-byte upload wiped a real
    node's content in production. A file that reads as empty must never reach
    http_client.post_bytes.

    Substitution probe: remove the `len(content_bytes) == 0` check -- the POST
    would be sent with an empty body and the no-HTTP assertion fails.
    """
    target = tmp_path / "empty.md"
    target.write_bytes(b"")
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=detect)

        result = await _call(server, {"id": _NODE_ID, "path": str(target)})

    assert result.get("isError") is True
    assert not http_called, "HTTP must NOT be called when the file reads as zero bytes."
    assert _error_code(result) == "file_empty"
