"""
Unit tests for divoid_download_content: binary round-trip + error paths.

These tests mock the HTTP transport layer (via respx) and the filesystem.
They assert:
  1. A successful GET with non-UTF-8 binary bytes writes the bytes byte-identical
     to the target path, and the return dict has the correct bytes_written /
     content_type / success shape.
  2. A 404 response produces isError=True and no file is written.
  3. Structural guards (node_id < 1, empty path) fire before any HTTP call.

No network calls and no DiVoid credentials are required.

Architecture reference: DiVoid task #6597.
"""

from __future__ import annotations

import os
from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.download_content import register as register_download_content

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"

_NODE_ID = 99

_CONTENT_URL = f"{_DUMMY_BASE}/nodes/{_NODE_ID}/content"

# Non-UTF-8 binary sequence: PNG magic bytes + a high byte that is invalid UTF-8.
# This is the payload used to prove no encoding/decoding occurs in the round-trip.
_BINARY_PAYLOAD = b"\x89PNG\r\n\x1a\n\xff\xd8\xff\xe0\x00\x10"


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="module")
def server() -> FastMCP:
    """
    Module-scoped FastMCP server with only divoid_download_content registered.

    Dummy config — no real credentials, no network. http_client initialised
    with the dummy base URL so respx can intercept all outbound requests.
    """
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY)
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)

    mcp_server = FastMCP("divoid-mcp-download-content-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_download_content(mcp_server)

    return mcp_server


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool("divoid_download_content", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


# ---------------------------------------------------------------------------
# Happy path: binary round-trip
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_binary_bytes_written_byte_identical(server: FastMCP, tmp_path: Any) -> None:
    """
    GET returns non-UTF-8 binary bytes → file on disk is byte-identical to the mock body.

    This is the load-bearing assertion for the whole tool: it proves that no
    UTF-8 decode/re-encode step is silently mangling the byte stream.

    Substitution probe: decode result.body as UTF-8 and re-encode before writing —
    the write raises UnicodeDecodeError (or produces replacement chars), and even if
    it somehow survived, the assertion `file_bytes == _BINARY_PAYLOAD` would fail.
    """
    target = tmp_path / "output.bin"

    with respx.mock(assert_all_called=True) as mock:
        mock.get(_CONTENT_URL).mock(
            return_value=httpx.Response(
                200,
                content=_BINARY_PAYLOAD,
                headers={"content-type": "image/png"},
            )
        )

        result = await _call(server, {"node_id": _NODE_ID, "path": str(target)})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    assert result.get("success") is True, f"Expected success=True, got: {result}"

    file_bytes = target.read_bytes()
    assert file_bytes == _BINARY_PAYLOAD, (
        f"File bytes differ from mock body — decoding/re-encoding occurred.\n"
        f"  expected: {_BINARY_PAYLOAD!r}\n"
        f"  actual:   {file_bytes!r}\n"
        "Substitution probe: any UTF-8 decode step would corrupt the high bytes."
    )


@pytest.mark.asyncio
async def test_return_dict_shape(server: FastMCP, tmp_path: Any) -> None:
    """
    Return dict has success=True, path (echoed), bytes_written, content_type.

    Substitution probe: omit content_type from the return dict — the assertion
    on 'content_type' in result fails, revealing the field was dropped.
    """
    target = tmp_path / "shape_check.bin"

    with respx.mock(assert_all_called=True) as mock:
        mock.get(_CONTENT_URL).mock(
            return_value=httpx.Response(
                200,
                content=_BINARY_PAYLOAD,
                headers={"content-type": "application/octet-stream"},
            )
        )

        result = await _call(server, {"node_id": _NODE_ID, "path": str(target)})

    assert result.get("success") is True
    assert result.get("path") == str(target), (
        f"Expected path={str(target)!r}, got {result.get('path')!r}. "
        "Substitution probe: path must be echoed verbatim from the tool argument."
    )
    assert result.get("bytes_written") == len(_BINARY_PAYLOAD), (
        f"Expected bytes_written={len(_BINARY_PAYLOAD)}, got {result.get('bytes_written')!r}. "
        "Substitution probe: bytes_written must be len(result.body) before any transform."
    )
    assert result.get("content_type") == "application/octet-stream", (
        f"Expected content_type='application/octet-stream', got {result.get('content_type')!r}. "
        "Substitution probe: content_type must come from the response Content-Type header."
    )


@pytest.mark.asyncio
async def test_parent_dirs_created(server: FastMCP, tmp_path: Any) -> None:
    """
    Writing to a path whose parent directory does not yet exist creates the parents.

    Substitution probe: remove os.makedirs from the tool — open() raises FileNotFoundError
    and the tool returns isError=True with write_failed; the success assertion fails.
    """
    target = tmp_path / "nested" / "deep" / "output.bin"
    assert not target.parent.exists(), "Precondition: parent must not exist before call"

    with respx.mock(assert_all_called=True) as mock:
        mock.get(_CONTENT_URL).mock(
            return_value=httpx.Response(200, content=b"\x00\x01\x02", headers={})
        )

        result = await _call(server, {"node_id": _NODE_ID, "path": str(target)})

    assert result.get("success") is True, f"Expected success after parent-dir creation, got: {result}"
    assert target.exists(), "File must exist after a successful download with auto-created parents."


# ---------------------------------------------------------------------------
# Error path: 404 → isError, no file written
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_404_returns_error_no_file_written(server: FastMCP, tmp_path: Any) -> None:
    """
    GET returns 404 with node-not-found body → isError=True, target file not created.

    Substitution probe: remove the 404 branch from the tool — the tool falls
    through to map_http_error which still returns isError=True (so that part
    passes), but the file might be partially written if bytes are processed first.
    The stricter proof is that the file is absent entirely.
    """
    target = tmp_path / "should_not_exist.bin"

    not_found_body = b'{"code":"data_entitynotfound","text":"\'Node\' with id \'99\' not found"}'

    with respx.mock(assert_all_called=True) as mock:
        mock.get(_CONTENT_URL).mock(
            return_value=httpx.Response(
                404,
                content=not_found_body,
                headers={"content-type": "application/json"},
            )
        )

        result = await _call(server, {"node_id": _NODE_ID, "path": str(target)})

    assert result.get("isError") is True, f"Expected isError=True for 404, got: {result}"
    assert not target.exists(), (
        "Target file must NOT be created when the GET returns 404.\n"
        "Substitution probe: writing before the status check would create the file."
    )
    text = result.get("content", [{}])[0].get("text", "")
    assert "node_not_found" in text, (
        f"Expected 'node_not_found' code in error text, got: {text!r}. "
        "Substitution probe: wrong error branch would give a different code."
    )


# ---------------------------------------------------------------------------
# Structural guards: reject before HTTP
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_non_positive_node_id_rejected_before_http(server: FastMCP, tmp_path: Any) -> None:
    """
    node_id=0 → isError=True with divoid_bad_request, no HTTP call.

    Substitution probe: remove the `node_id < 1` check — the HTTP mock would be
    called (or a different URL hit), and the assertion on isError/no-HTTP fails.
    """
    target = tmp_path / "noop.bin"
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, content=b"irrelevant")

        mock.get(f"{_DUMMY_BASE}/nodes/0/content").mock(side_effect=detect)

        result = await _call(server, {"node_id": 0, "path": str(target)})

    assert result.get("isError") is True, f"Expected isError=True for node_id=0, got: {result}"
    assert not http_called, "HTTP must NOT be called when the structural guard fires."
    text = result.get("content", [{}])[0].get("text", "")
    assert "divoid_bad_request" in text, f"Expected 'divoid_bad_request', got: {text!r}"


@pytest.mark.asyncio
async def test_empty_path_rejected_before_http(server: FastMCP) -> None:
    """
    path='' → isError=True with divoid_bad_request, no HTTP call.

    Substitution probe: remove the empty-path check — open('', 'wb') raises
    FileNotFoundError which becomes a write_failed error, a different code.
    """
    http_called = False

    with respx.mock(assert_all_called=False) as mock:
        def detect(req: httpx.Request) -> httpx.Response:
            nonlocal http_called
            http_called = True
            return httpx.Response(200, content=b"data")

        mock.get(_CONTENT_URL).mock(side_effect=detect)

        result = await _call(server, {"node_id": _NODE_ID, "path": ""})

    assert result.get("isError") is True, f"Expected isError=True for empty path, got: {result}"
    assert not http_called, "HTTP must NOT be called when the empty-path guard fires."
    text = result.get("content", [{}])[0].get("text", "")
    assert "divoid_bad_request" in text, f"Expected 'divoid_bad_request', got: {text!r}"
