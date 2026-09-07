"""
Unit tests for tools/_content.py: the shared exclusivity guard and body
resolution used by divoid_set_content and the four composite create tools.
"""

from __future__ import annotations

from typing import Any

import pytest

from divoid_mcp import paths
from divoid_mcp.errors import InvariantViolation
from divoid_mcp.tools._content import guard_exclusive, resolve_body


@pytest.fixture(autouse=True)
def _configure_root(tmp_path: Any) -> None:
    """Configures tmp_path as the sole filesystem root for each test."""
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(tmp_path)})


def _error_code(envelope: dict[str, Any]) -> str:
    text = envelope["content"][0]["text"]
    return text.split(":", 1)[0]


def test_guard_exclusive_raises_on_both_given() -> None:
    with pytest.raises(InvariantViolation) as excinfo:
        guard_exclusive("inline", "/some/path")
    assert excinfo.value.code == "content_path_conflict"


def test_guard_exclusive_silent_on_content_only() -> None:
    guard_exclusive("inline", None)


def test_guard_exclusive_silent_on_path_only() -> None:
    guard_exclusive(None, "/some/path")


def test_guard_exclusive_silent_on_neither() -> None:
    guard_exclusive(None, None)


def test_resolve_body_content_given_encodes_utf8() -> None:
    content_bytes, err = resolve_body("hello über", None, "test-label")
    assert err is None
    assert content_bytes == "hello über".encode("utf-8")


def test_resolve_body_both_absent_yields_no_body_not_a_refusal() -> None:
    content_bytes, err = resolve_body(None, None, "test-label")
    assert content_bytes is None
    assert err is None


def test_resolve_body_empty_path_string_refused() -> None:
    content_bytes, err = resolve_body(None, "", "test-label")
    assert content_bytes is None
    assert err is not None
    assert err["isError"] is True
    assert _error_code(err) == "path_empty"


def test_resolve_body_missing_file_refused(tmp_path: Any) -> None:
    target = tmp_path / "does_not_exist.md"
    content_bytes, err = resolve_body(None, str(target), "test-label")
    assert content_bytes is None
    assert _error_code(err) == "file_not_found"


def test_resolve_body_directory_path_refused(tmp_path: Any) -> None:
    target = tmp_path / "a_directory"
    target.mkdir()
    content_bytes, err = resolve_body(None, str(target), "test-label")
    assert content_bytes is None
    assert _error_code(err) == "file_read_failed"


def test_resolve_body_zero_byte_file_refused(tmp_path: Any) -> None:
    target = tmp_path / "empty.md"
    target.write_bytes(b"")
    content_bytes, err = resolve_body(None, str(target), "test-label")
    assert content_bytes is None
    assert _error_code(err) == "file_empty"


def test_resolve_body_whitespace_only_file_uploaded_as_is(tmp_path: Any) -> None:
    """A whitespace-only FILE is uploaded verbatim, unlike whitespace-only
    inline content (which each tool's own required-check refuses before
    resolve_body is ever reached)."""
    target = tmp_path / "whitespace.md"
    target.write_bytes(b"   \n  ")
    content_bytes, err = resolve_body(None, str(target), "test-label")
    assert err is None
    assert content_bytes == b"   \n  "


def test_resolve_body_out_of_root_path_refused(tmp_path: Any) -> None:
    root_dir = tmp_path / "workspace"
    evil_dir = tmp_path / "workspace-evil"
    root_dir.mkdir()
    evil_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    secret = evil_dir / "secret.txt"
    secret.write_bytes(b"should never leave this directory")

    content_bytes, err = resolve_body(None, str(secret), "test-label")
    assert content_bytes is None
    assert _error_code(err) == "path_outside_root"


def test_resolve_body_sensitive_in_root_path_refused(tmp_path: Any) -> None:
    root_dir = tmp_path / "workspace"
    git_dir = root_dir / ".git"
    git_dir.mkdir(parents=True)
    target = git_dir / "config"
    target.write_bytes(b"[remote]token=shhh")
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    content_bytes, err = resolve_body(None, str(target), "test-label")
    assert content_bytes is None
    assert _error_code(err) == "path_denied_sensitive"


def test_resolve_body_opens_the_resolved_path_not_the_raw_caller_string(
    tmp_path: Any, monkeypatch: Any
) -> None:
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

    content_bytes, err = resolve_body(None, raw_relative, "test-label")

    assert err is None
    assert expected_resolved in captured_files
    assert raw_relative not in captured_files


def test_resolve_body_never_opens_a_sensitive_file(tmp_path: Any, monkeypatch: Any) -> None:
    root_dir = tmp_path / "workspace"
    git_dir = root_dir / ".git"
    git_dir.mkdir(parents=True)
    target = git_dir / "config"
    target.write_bytes(b"[remote]token=shhh")
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    open_called = False
    real_open = open

    def spy_open(file, *args, **kwargs):
        nonlocal open_called
        if str(file) == str(target):
            open_called = True
        return real_open(file, *args, **kwargs)

    monkeypatch.setattr("builtins.open", spy_open)

    content_bytes, err = resolve_body(None, str(target), "test-label")

    assert content_bytes is None
    assert _error_code(err) == "path_denied_sensitive"
    assert not open_called


def test_resolve_body_no_usable_root_returns_file_root_unusable(tmp_path: Any) -> None:
    paths._roots = ()
    target = tmp_path / "would_be_fine.md"
    target.write_bytes(b"content")

    content_bytes, err = resolve_body(None, str(target), "test-label")
    assert content_bytes is None
    assert _error_code(err) == "file_root_unusable"


def test_resolve_body_content_takes_priority_when_both_given() -> None:
    """resolve_body itself does not enforce exclusivity (that is guard_exclusive's
    job); documents the actual precedence if called with both anyway."""
    content_bytes, err = resolve_body("inline body", "/some/path/never/read", "test-label")
    assert err is None
    assert content_bytes == b"inline body"
