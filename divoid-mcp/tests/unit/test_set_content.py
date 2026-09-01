"""
Unit tests for divoid_set_content: inline/file mutual exclusion, byte-identical
file upload, and the file-read guards. No network calls; respx mocks the HTTP layer.

Architecture reference: DiVoid #8523 §Defect 2, #7895 Finding 1.
"""

from __future__ import annotations

from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client, paths
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.set_content import _execute as _execute_set_content
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


@pytest.fixture(autouse=True)
def _configure_root(tmp_path: Any) -> None:
    """Configures tmp_path as the sole filesystem root for each test."""
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(tmp_path)})


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool("divoid_set_content", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


def _error_code(result: dict[str, Any]) -> str:
    text = result.get("content", [{}])[0].get("text", "")
    return text.split(":", 1)[0]


@pytest.mark.asyncio
async def test_inline_content_still_posts_utf8_bytes(server: FastMCP) -> None:
    """The pre-existing inline `content` path posts UTF-8 bytes unchanged."""
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
    """Whitespace-only `content` is rejected as content_empty before any HTTP call."""
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


@pytest.mark.asyncio
async def test_path_uploads_trap_bytes_byte_identical(server: FastMCP, tmp_path: Any) -> None:
    """A file with an unescaped '|', CRLF, a multi-byte char, and a lone CR
    uploads byte-for-byte identical."""
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
    """A non-UTF-8 binary file uploads byte-identical, and an explicit
    content_type override reaches the wire unchanged."""
    payload = b"\x89PNG\r\n\x1a\n\xff\xd8\xff\xe0\x00\x10"
    target = tmp_path / "image.bin"
    target.write_bytes(payload)

    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(req)
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=capture)

        result = await _call(
            server,
            {"id": _NODE_ID, "path": str(target), "content_type": "application/octet-stream"},
        )

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert captured[0].content == payload, (
        f"Expected byte-identical binary upload, got: {captured[0].content!r}"
    )
    assert captured[0].headers["content-type"] == "application/octet-stream", (
        f"Expected the override to reach the wire, got: {captured[0].headers['content-type']!r}"
    )


@pytest.mark.asyncio
async def test_path_uses_default_content_type_when_not_overridden(
    server: FastMCP, tmp_path: Any
) -> None:
    """`path` with no content_type override sends the same default
    Content-Type as the inline path, verified on the wire."""
    target = tmp_path / "doc.md"
    target.write_bytes(b"# heading\n")

    captured: list[httpx.Request] = []

    with respx.mock(assert_all_called=True) as mock:
        def capture(req: httpx.Request) -> httpx.Response:
            captured.append(req)
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=capture)

        result = await _call(server, {"id": _NODE_ID, "path": str(target)})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert captured[0].headers["content-type"] == "text/markdown; charset=utf-8", (
        f"Expected the default Content-Type on the wire, "
        f"got: {captured[0].headers['content-type']!r}"
    )
    assert result.get("content_type") == "text/markdown; charset=utf-8"


@pytest.mark.asyncio
async def test_both_content_and_path_rejected_before_http(server: FastMCP, tmp_path: Any) -> None:
    """content AND path both given is rejected as content_path_conflict before any HTTP call."""
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
    """Neither content nor path given is rejected as content_path_required before any HTTP call."""
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
    """An empty `path` string is rejected as path_empty before any HTTP call."""
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


@pytest.mark.asyncio
async def test_missing_file_rejected_before_http(server: FastMCP, tmp_path: Any) -> None:
    """A path to a nonexistent file is rejected as file_not_found before any HTTP call."""
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
    """A path pointing at a directory is rejected as file_read_failed before any HTTP call."""
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
    """A zero-byte file is rejected as file_empty before any HTTP call (#7872 regression guard)."""
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


@pytest.mark.asyncio
async def test_out_of_root_path_rejected_before_http_or_open(
    server: FastMCP, tmp_path: Any
) -> None:
    root_dir = tmp_path / "workspace"
    evil_dir = tmp_path / "workspace-evil"
    root_dir.mkdir()
    evil_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    secret = evil_dir / "secret.txt"
    secret.write_bytes(b"should never leave this directory")
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=detect)

        result = await _call(server, {"id": _NODE_ID, "path": str(secret)})

    assert result.get("isError") is True, f"Expected isError=True, got: {result}"
    assert not http_called, (
        "HTTP must NOT be called when the path gate rejects -- the file's bytes "
        "must never even be read into the process, let alone posted."
    )
    assert _error_code(result) == "path_outside_root"


@pytest.mark.asyncio
async def test_execute_called_directly_with_out_of_root_path_is_still_rejected(
    tmp_path: Any,
) -> None:
    """Calls _execute() directly, bypassing register()'s wrapper and _check_invariants."""
    root_dir = tmp_path / "workspace"
    evil_dir = tmp_path / "workspace-evil"
    root_dir.mkdir()
    evil_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    secret = evil_dir / "secret.txt"
    secret.write_bytes(b"should never leave this directory")

    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)

    with respx.mock(assert_all_called=False) as mock:
        http_called = False

        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=detect)

        result = await _execute_set_content(id=_NODE_ID, config=config, path=str(secret))

    assert result.get("isError") is True, (
        f"Expected isError=True from a direct _execute() call with an out-of-root "
        f"path, got: {result}"
    )
    assert not http_called
    assert _error_code(result) == "path_outside_root"


@pytest.mark.asyncio
async def test_execute_opens_the_resolved_path_not_the_raw_caller_string(
    tmp_path: Any, monkeypatch: Any
) -> None:
    """Spies on the builtin open() to capture the exact argument _execute calls it with."""
    root_dir = tmp_path / "workspace"
    root_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    target = root_dir / "doc.md"
    target.write_bytes(b"# hi\n")

    monkeypatch.chdir(root_dir)
    raw_relative = "doc.md"
    expected_resolved = paths.gate(raw_relative)

    captured_files: list[Any] = []
    real_open = open

    def spy_open(file, *args, **kwargs):
        captured_files.append(file)
        return real_open(file, *args, **kwargs)

    monkeypatch.setattr("builtins.open", spy_open)

    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    with respx.mock(assert_all_called=True) as mock:
        mock.post(_CONTENT_URL).mock(return_value=httpx.Response(200, json={"id": _NODE_ID}))
        result = await _execute_set_content(id=_NODE_ID, config=config, path=raw_relative)

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert expected_resolved in captured_files, (
        f"Expected open() to be called with the resolved path {expected_resolved!r}, "
        f"but it was called with: {captured_files!r}"
    )
    assert raw_relative not in captured_files, (
        f"open() must not be called with the raw caller string {raw_relative!r} "
        f"directly -- got: {captured_files!r}"
    )


@pytest.mark.asyncio
async def test_no_usable_root_returns_file_root_unusable(server: FastMCP, tmp_path: Any) -> None:
    paths._roots = ()
    target = tmp_path / "would_be_fine.md"
    target.write_bytes(b"content")
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, json={"id": _NODE_ID})

        mock.post(_CONTENT_URL).mock(side_effect=detect)

        result = await _call(server, {"id": _NODE_ID, "path": str(target)})

    assert result.get("isError") is True
    assert not http_called
    assert _error_code(result) == "file_root_unusable"
