"""Unit tests for divoid_mcp.paths -- the filesystem-path containment gate."""

from __future__ import annotations

import os

import pytest

from divoid_mcp import paths
from divoid_mcp.errors import InvariantViolation


def test_init_defaults_to_cwd_when_env_unset(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    paths.init(env={})

    accepted = paths.gate(str(tmp_path / "ok.txt"))
    assert os.path.normcase(accepted).startswith(os.path.normcase(str(tmp_path))[:2]), (
        "resolved path should be an absolute path on the same drive as tmp_path"
    )


def test_init_env_var_overrides_cwd(tmp_path, monkeypatch):
    """Env var wins over cwd: a path under cwd is rejected, a path under the configured root is accepted."""
    root_dir = tmp_path / "configured_root"
    other_dir = tmp_path / "elsewhere_cwd"
    root_dir.mkdir()
    other_dir.mkdir()
    monkeypatch.chdir(other_dir)

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    paths.gate(str(root_dir / "ok.txt"))

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(str(other_dir / "nope.txt"))
    assert exc_info.value.code == "path_outside_root"


def test_init_with_no_env_argument_reads_real_os_environ(tmp_path, monkeypatch):
    monkeypatch.setenv("DIVOID_MCP_FILE_ROOT", str(tmp_path))

    paths.init()

    paths.gate(str(tmp_path / "ok.txt"))


def test_is_usable_root_tolerates_home_resolution_failure(tmp_path, monkeypatch):
    real_realpath = paths.os.path.realpath

    def flaky_realpath(path: str) -> str:
        if path == os.path.expanduser("~"):
            raise OSError("synthetic: home directory cannot be resolved")
        return real_realpath(path)

    monkeypatch.setattr(paths.os.path, "realpath", flaky_realpath)

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(tmp_path)})

    assert paths.roots() != (), "a legitimate non-home candidate must still be accepted"
    paths.gate(str(tmp_path / "ok.txt"))


def test_init_multiple_roots_pathsep_separated(tmp_path):
    root_a = tmp_path / "root_a"
    root_b = tmp_path / "root_b"
    root_a.mkdir()
    root_b.mkdir()

    paths.init(env={"DIVOID_MCP_FILE_ROOT": os.pathsep.join([str(root_a), str(root_b)])})

    paths.gate(str(root_a / "x.txt"))
    paths.gate(str(root_b / "y.txt"))
    assert len(paths.roots()) == 2, f"expected 2 roots, got {paths.roots()!r}"


def test_init_rejects_drive_root_candidate(caplog):
    drive_root = os.path.splitdrive(os.getcwd())[0] + os.sep
    with caplog.at_level("WARNING"):
        paths.init(env={"DIVOID_MCP_FILE_ROOT": drive_root})

    assert paths.roots() == (), f"expected no usable roots, got {paths.roots()!r}"
    assert any("DIVOID_MCP_FILE_ROOT" in rec.message for rec in caplog.records), (
        "expected a WARNING naming the env var when the only candidate root is unusable"
    )

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(str(os.path.join(drive_root, "anything.txt")))
    assert exc_info.value.code == "file_root_unusable"


def test_init_rejects_home_directory_candidate(tmp_path, monkeypatch):
    fake_home = tmp_path / "fake_home"
    fake_home.mkdir()
    monkeypatch.setenv("USERPROFILE", str(fake_home))
    monkeypatch.setenv("HOME", str(fake_home))

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(fake_home)})

    assert paths.roots() == (), f"expected home directory to be rejected, got {paths.roots()!r}"

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(str(fake_home / "secrets" / ".divoid-online"))
    assert exc_info.value.code == "file_root_unusable"


def test_init_unresolvable_root_candidate_discarded(caplog, monkeypatch):
    """Uses a monkeypatched realpath: an embedded-NUL input did not reliably raise on this platform."""
    def boom(_path: str) -> str:
        raise ValueError("synthetic: this candidate cannot be resolved")

    monkeypatch.setattr(paths.os.path, "realpath", boom)

    with caplog.at_level("WARNING"):
        paths.init(env={"DIVOID_MCP_FILE_ROOT": "whatever-candidate"})

    assert paths.roots() == ()
    monkeypatch.undo()

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate("anything.txt")
    assert exc_info.value.code == "file_root_unusable"


def test_gate_raises_file_root_unusable_when_roots_empty():
    assert paths.roots() == ()

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate("C:\\anything\\at\\all.txt")
    assert exc_info.value.code == "file_root_unusable"


def test_gate_accepts_path_inside_root(tmp_path):
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(tmp_path)})
    target = tmp_path / "ok.txt"
    target.write_bytes(b"hello")

    resolved = paths.gate(str(target))

    assert os.path.samefile(resolved, target), (
        f"resolved path {resolved!r} does not identify the same file as {target!r}"
    )


def test_gate_accepts_relative_path_resolved_against_cwd(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    paths.init(env={})

    (tmp_path / "relative.txt").write_bytes(b"x")
    resolved = paths.gate("relative.txt")

    assert os.path.samefile(resolved, tmp_path / "relative.txt")


def test_gate_accepts_case_variant_of_path_realpath_can_canonicalize(tmp_path):
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(tmp_path)})
    sub = tmp_path / "Sub"
    sub.mkdir()
    target = sub / "ok.txt"
    target.write_bytes(b"x")

    upper_variant = str(target).upper()
    resolved = paths.gate(upper_variant)

    assert os.path.samefile(resolved, target)


def test_gate_normcase_folds_case_mismatch_on_non_existent_root_and_tail(tmp_path):
    """Root and candidate tail both non-existent, so realpath cannot canonicalize case on disk; only normcase makes them compare equal."""
    root_dir = tmp_path / "NonExistentRoot"
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    candidate = str(tmp_path / "nonexistentroot" / "file.txt")
    resolved = paths.gate(candidate)

    assert os.path.normcase(os.path.dirname(resolved)) == os.path.normcase(str(root_dir))


def test_gate_accepts_drive_relative_path_under_cwd(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    paths.init(env={})

    drive = os.path.splitdrive(str(tmp_path))[0]
    if not drive:
        pytest.skip("tmp_path has no drive letter on this platform")

    (tmp_path / "foo").mkdir()
    (tmp_path / "foo" / "bar.txt").write_bytes(b"x")

    resolved = paths.gate(f"{drive}foo\\bar.txt")

    assert os.path.samefile(resolved, tmp_path / "foo" / "bar.txt")


def test_gate_rejects_sibling_directory_with_extended_name(tmp_path):
    root_dir = tmp_path / "divoid"
    evil_dir = tmp_path / "divoid-evil"
    root_dir.mkdir()
    evil_dir.mkdir()

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(str(evil_dir / "x.txt"))
    assert exc_info.value.code == "path_outside_root"


def test_gate_rejects_traversal_that_escapes_root(tmp_path):
    root_dir = tmp_path / "workspace"
    root_dir.mkdir()
    outside_dir = tmp_path / "Windows" / "Temp"
    outside_dir.mkdir(parents=True)

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    traversal = str(root_dir) + "\\..\\Windows\\Temp\\x.txt"
    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(traversal)
    assert exc_info.value.code == "path_outside_root"


def test_gate_accepts_non_existent_tail_lexically(tmp_path):
    root_dir = tmp_path / "workspace"
    root_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    candidate = str(root_dir / "deep" / "not" / "exist" / "yet" / "x.txt")
    resolved = paths.gate(candidate)

    assert os.path.normcase(os.path.dirname(resolved)).startswith(
        os.path.normcase(str(root_dir))
    )


@pytest.mark.parametrize(
    "prefix",
    ["\\\\?\\", "\\\\.\\", "//?/", "//./"],
)
def test_gate_rejects_extended_length_and_device_prefixes(tmp_path, prefix, monkeypatch):
    """Also asserts realpath is never called, since the rejection code alone does not discriminate reliably across environments."""
    root_dir = tmp_path / "workspace"
    root_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    real_realpath = paths.os.path.realpath
    call_count = {"n": 0}

    def spy(path: str) -> str:
        call_count["n"] += 1
        return real_realpath(path)

    monkeypatch.setattr(paths.os.path, "realpath", spy)

    candidate = prefix + "C:\\Windows\\Temp\\x.txt"
    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(candidate)
    assert exc_info.value.code == "path_outside_root"
    assert call_count["n"] == 0, (
        "os.path.realpath must not be called at all for a rejected extended-length/"
        f"device prefix -- the syntactic pre-filter must reject it first (called {call_count['n']}x)"
    )


def test_gate_rejects_extended_prefix_without_ever_resolving_it(tmp_path, monkeypatch):
    """Confirmed by realpath call-count, not error code, since the resolved-and-rejected outcome is not distinguishable from a syntactic rejection on every platform."""
    root_dir = tmp_path / "workspace"
    root_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    real_realpath = paths.os.path.realpath
    call_count = {"n": 0}

    def spy(path: str) -> str:
        call_count["n"] += 1
        return real_realpath(path)

    monkeypatch.setattr(paths.os.path, "realpath", spy)

    candidate = "\\\\?\\" + str(root_dir) + r"\..\..\Windows\x"
    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(candidate)
    assert exc_info.value.code == "path_outside_root"
    assert call_count["n"] == 0, (
        f"os.path.realpath must not be called at all for a \\\\?\\ prefixed input "
        f"(called {call_count['n']}x)"
    )


def test_gate_rejects_embedded_nul_byte(tmp_path):
    root_dir = tmp_path / "workspace"
    root_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    candidate = str(root_dir / "a\x00b.txt")
    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(candidate)
    assert exc_info.value.code == "path_outside_root"


def test_gate_accepts_in_root_path_without_nul_byte(tmp_path):
    root_dir = tmp_path / "workspace"
    root_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    target = root_dir / "ab.txt"
    target.write_bytes(b"x")

    resolved = paths.gate(str(target))
    assert os.path.samefile(resolved, target)


def test_gate_rejects_unc_path(tmp_path):
    root_dir = tmp_path / "workspace"
    root_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(r"\\server\share\x.txt")
    assert exc_info.value.code == "path_outside_root"


def test_gate_rejects_cross_drive_path(tmp_path):
    root_dir = tmp_path / "workspace"
    root_dir.mkdir()
    own_drive = os.path.splitdrive(str(root_dir))[0]
    other_drive = "Z:" if own_drive.upper() != "Z:" else "Y:"

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(f"{other_drive}\\elsewhere\\x.txt")
    assert exc_info.value.code == "path_outside_root"


def test_gate_treats_realpath_exception_as_rejection(tmp_path, monkeypatch):
    """Any exception during resolution is a rejection, not only OSError."""
    root_dir = tmp_path / "workspace"
    root_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    def boom(_path: str) -> str:
        raise ValueError("synthetic resolution failure -- not an OSError")

    monkeypatch.setattr(paths.os.path, "realpath", boom)

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(str(root_dir / "x.txt"))
    assert exc_info.value.code == "path_outside_root"


def test_gate_treats_commonpath_exception_as_rejection_not_crash(tmp_path, monkeypatch):
    root_dir = tmp_path / "workspace"
    root_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    def boom(_paths: list[str]) -> str:
        raise ValueError("synthetic commonpath failure")

    monkeypatch.setattr(paths.os.path, "commonpath", boom)

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(str(root_dir / "x.txt"))
    assert exc_info.value.code == "path_outside_root"


def test_gate_rejects_path_that_is_outside_every_configured_root(tmp_path):
    root_dir = tmp_path / "workspace"
    outside_dir = tmp_path / "not_workspace"
    root_dir.mkdir()
    outside_dir.mkdir()

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(str(outside_dir / "x.txt"))
    assert exc_info.value.code == "path_outside_root"
