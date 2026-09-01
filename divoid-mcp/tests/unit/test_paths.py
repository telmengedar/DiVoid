"""Unit tests for divoid_mcp.paths -- the filesystem-path containment gate."""

from __future__ import annotations

import ctypes
import os
import subprocess
import sys

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


def test_gate_rejects_dot_git_config_at_root_top_level(tmp_path):
    root_dir = tmp_path / "workspace"
    git_dir = root_dir / ".git"
    git_dir.mkdir(parents=True)
    (git_dir / "config").write_bytes(b"[remote]")

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(str(git_dir / "config"))
    assert exc_info.value.code == "path_denied_sensitive"


def test_gate_rejects_nested_dot_git_config(tmp_path):
    """A nested git dir is equally credentialed -- the rule must not be anchored at root top level only."""
    root_dir = tmp_path / "workspace"
    nested_git = root_dir / "sub" / ".git"
    nested_git.mkdir(parents=True)
    (nested_git / "config").write_bytes(b"[remote]")

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(str(nested_git / "config"))
    assert exc_info.value.code == "path_denied_sensitive"


def test_gate_rejects_dot_git_config_via_dot_dot_collapse(tmp_path):
    """A rule applied before '..' collapsing would miss this -- the resolved form must be used."""
    root_dir = tmp_path / "workspace"
    git_dir = root_dir / ".git"
    git_dir.mkdir(parents=True)
    (git_dir / "config").write_bytes(b"[remote]")

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    traversal = str(git_dir) + os.sep + os.pardir + os.sep + ".git" + os.sep + "config"
    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(traversal)
    assert exc_info.value.code == "path_denied_sensitive"


@pytest.mark.skipif(sys.platform != "win32", reason="directory junctions are a Windows NTFS feature")
def test_gate_rejects_in_root_junction_pointing_at_dot_git(tmp_path):
    """A raw-string check is defeated by an in-root junction; a resolved-form check is not."""
    root_dir = tmp_path / "workspace"
    git_dir = root_dir / ".git"
    git_dir.mkdir(parents=True)
    (git_dir / "config").write_bytes(b"[remote]")
    junction = root_dir / "fakedir"

    subprocess.run(
        ["cmd", "/c", "mklink", "/J", str(junction), str(git_dir)],
        capture_output=True,
        check=True,
    )

    try:
        raw_candidate = str(junction / "config")
        assert ".git" not in raw_candidate.split(os.sep), (
            "the raw caller string must not itself contain a '.git' component, or this "
            "test cannot distinguish resolved-form matching from raw-string matching"
        )

        paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

        with pytest.raises(InvariantViolation) as exc_info:
            paths.gate(raw_candidate)
        assert exc_info.value.code == "path_denied_sensitive"
    finally:
        os.rmdir(str(junction))


def _literal_relpath(path: str, start: str) -> str:
    """Test-only relpath stand-in: pure string slicing, no OS call -- unlike the real
    os.path.relpath, which internally calls abspath() and, on Windows, ends up invoking
    GetFullPathNameW -- the same Win32 API whose trailing-dot/space normalisation is
    what this property test needs to route AROUND, not exercise."""
    assert path.startswith(start), "test helper precondition: path must start with start"
    return path[len(start):].lstrip(os.sep)


def test_gate_rejects_trailing_dot_component_as_a_property_not_a_platform_behavior(
    tmp_path, monkeypatch
):
    """
    Forces realpath AND relpath stand-ins that do NOT collapse a trailing dot on a
    path component. Measured: os.path.normpath does not touch trailing dots/spaces,
    but the real os.path.relpath does (it calls abspath() -> GetFullPathNameW
    internally on Windows) -- so a naive test that mutates only realpath would
    still pass even with the module's own .rstrip(". ") deleted, proving nothing.
    Routing around both is what actually isolates this module's own stripping from
    platform behaviour that a future interpreter version is not obligated to keep.
    """
    root_dir = tmp_path / "workspace"
    root_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    def non_normalising_realpath(p: str) -> str:
        return os.path.normpath(os.path.join(os.getcwd(), p) if not os.path.isabs(p) else p)

    candidate = str(root_dir) + os.sep + ".git." + os.sep + "config"
    resolved = non_normalising_realpath(candidate)
    assert resolved.endswith(os.sep + ".git." + os.sep + "config"), (
        "the stand-in realpath must preserve the trailing dot, or this test proves nothing"
    )
    assert _literal_relpath(resolved, str(root_dir)).split(os.sep)[0] == ".git.", (
        "the stand-in relpath must preserve the trailing dot, or this test proves nothing"
    )

    monkeypatch.setattr(paths.os.path, "realpath", non_normalising_realpath)
    monkeypatch.setattr(paths.os.path, "relpath", _literal_relpath)

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(candidate)
    assert exc_info.value.code == "path_denied_sensitive"


def test_gate_rejects_trailing_space_component_as_a_property_not_a_platform_behavior(
    tmp_path, monkeypatch
):
    root_dir = tmp_path / "workspace"
    root_dir.mkdir()
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    def non_normalising_realpath(p: str) -> str:
        return os.path.normpath(os.path.join(os.getcwd(), p) if not os.path.isabs(p) else p)

    candidate = str(root_dir) + os.sep + ".git " + os.sep + "config"
    resolved = non_normalising_realpath(candidate)
    assert resolved.endswith(os.sep + ".git " + os.sep + "config"), (
        "the stand-in realpath must preserve the trailing space, or this test proves nothing"
    )
    assert _literal_relpath(resolved, str(root_dir)).split(os.sep)[0] == ".git ", (
        "the stand-in relpath must preserve the trailing space, or this test proves nothing"
    )

    monkeypatch.setattr(paths.os.path, "realpath", non_normalising_realpath)
    monkeypatch.setattr(paths.os.path, "relpath", _literal_relpath)

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(candidate)
    assert exc_info.value.code == "path_denied_sensitive"


def test_gate_rejects_dot_git_via_8dot3_short_name(tmp_path):
    """8.3 short-name generation resolves before the pattern match, so a short-name
    spelling of .git cannot bypass the refusal. Skipped if 8dot3 is disabled on this volume."""
    root_dir = tmp_path / "workspace"
    git_dir = root_dir / ".git"
    git_dir.mkdir(parents=True)
    (git_dir / "config").write_bytes(b"[remote]")

    buf = ctypes.create_unicode_buffer(260)
    n = ctypes.windll.kernel32.GetShortPathNameW(str(git_dir), buf, 260)
    if not n or os.path.normcase(buf.value) == os.path.normcase(str(git_dir)):
        pytest.skip("8dot3 short-name generation is disabled on this volume")

    short_candidate = os.path.join(buf.value, "config")
    assert ".git" not in short_candidate.split(os.sep), (
        "the short-name candidate must not itself spell out '.git', or this test "
        "cannot distinguish short-name resolution from a literal match"
    )

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(short_candidate)
    assert exc_info.value.code == "path_denied_sensitive"


@pytest.mark.parametrize(
    "relative_target,pattern",
    [
        (os.path.join(".env"), ".env*"),
        (os.path.join("certs", "server.pem"), "*.pem"),
        (os.path.join(".claude", "settings.json"), "settings.json"),
    ],
)
def test_gate_rejects_named_sensitive_families(tmp_path, relative_target, pattern):
    root_dir = tmp_path / "workspace"
    target = root_dir / relative_target
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(b"secret")

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(str(target))
    assert exc_info.value.code == "path_denied_sensitive"
    assert pattern in exc_info.value.message


@pytest.mark.parametrize(
    "relative_target",
    [
        os.path.join(".npmrc"),
        os.path.join(".pypirc"),
        os.path.join(".netrc"),
        os.path.join("pip.conf"),
        os.path.join("pip.ini"),
        os.path.join("keys", "server.key"),
        os.path.join("keys", "bundle.pfx"),
        os.path.join("keys", "bundle.p12"),
        os.path.join(".ssh", "id_dsa"),
        os.path.join(".ssh", "id_ecdsa"),
        os.path.join(".ssh", "id_ed25519"),
        os.path.join(".mcp.json"),
        os.path.join(".claude", "settings.local.json"),
    ],
)
def test_gate_rejects_remaining_sensitive_patterns(tmp_path, relative_target):
    root_dir = tmp_path / "workspace"
    target = root_dir / relative_target
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(b"secret")

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(str(target))
    assert exc_info.value.code == "path_denied_sensitive"


def test_gate_allows_dot_claude_file_that_is_not_individually_named(tmp_path):
    """.claude/ is not denied wholesale -- only the two named files inside it are."""
    root_dir = tmp_path / "workspace"
    target = root_dir / ".claude" / "agents" / "jenny.md"
    target.parent.mkdir(parents=True)
    target.write_bytes(b"agent notes")

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    resolved = paths.gate(str(target))
    assert os.path.samefile(resolved, target)


def test_gate_allows_worktree_rooted_session_despite_sensitive_named_ancestor(tmp_path):
    """
    A session rooted at <repo>/.claude/worktrees/<purpose> (where isolation:
    'worktree' puts worktrees) must not refuse everything. This root sits under
    an ancestor directory that matches a sensitive pattern (.env), which is the
    real falsifier of absolute-component matching with this predicate's actual
    pattern list: a correct implementation judges components relative to the
    matched root and never looks above it; a buggy absolute-path implementation
    would incorrectly refuse every call in such a session.
    """
    ancestor = tmp_path / ".env" / ".claude" / "worktrees" / "purpose"
    target = ancestor / "docs" / "note.md"
    target.parent.mkdir(parents=True)
    target.write_bytes(b"note")

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(ancestor)})

    resolved = paths.gate(str(target))
    assert os.path.samefile(resolved, target)


@pytest.mark.parametrize(
    "relative_target",
    [
        os.path.join(".github", "workflows", "ci.yml"),
        os.path.join("src", ".gitignore"),
        os.path.join("src", ".gitattributes"),
    ],
)
def test_gate_allows_git_prefixed_names_that_are_not_whole_component_matches(
    tmp_path, relative_target
):
    """A '.git'-prefix match instead of a whole-component match would refuse all three."""
    root_dir = tmp_path / "workspace"
    target = root_dir / relative_target
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(b"content")

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    resolved = paths.gate(str(target))
    assert os.path.samefile(resolved, target)


def test_gate_allows_id_generator_py_despite_id_underscore_prefix(tmp_path):
    """An id_* glob would match this file -- the four SSH key names are listed literally."""
    root_dir = tmp_path / "workspace"
    target = root_dir / "tools" / "id_generator.py"
    target.parent.mkdir(parents=True)
    target.write_bytes(b"# generates ids")

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    resolved = paths.gate(str(target))
    assert os.path.samefile(resolved, target)


def test_gate_rejects_id_rsa_but_allows_id_rsa_pub(tmp_path):
    """The clearest single demonstration of the precision bar: the private key is
    refused, its harmless public half is not, and over-matching would collapse the pair."""
    root_dir = tmp_path / "workspace"
    ssh_dir = root_dir / ".ssh"
    ssh_dir.mkdir(parents=True)
    private_key = ssh_dir / "id_rsa"
    public_key = ssh_dir / "id_rsa.pub"
    private_key.write_bytes(b"-----BEGIN OPENSSH PRIVATE KEY-----")
    public_key.write_bytes(b"ssh-rsa AAAA...")

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(str(private_key))
    assert exc_info.value.code == "path_denied_sensitive"

    resolved = paths.gate(str(public_key))
    assert os.path.samefile(resolved, public_key)


@pytest.mark.parametrize(
    "relative_target",
    [
        os.path.join("docs", "environment.md"),
        os.path.join("docs", "gitignore-notes.md"),
        os.path.join("monkey.py"),
    ],
)
def test_gate_allows_names_with_a_sensitive_substring_but_not_a_whole_component_match(
    tmp_path, relative_target
):
    """Substring matching anywhere in the name would refuse all three of these."""
    root_dir = tmp_path / "workspace"
    target = root_dir / relative_target
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(b"content")

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    resolved = paths.gate(str(target))
    assert os.path.samefile(resolved, target)


def test_gate_allows_hardlink_alias_of_denied_file_known_unclosed_limit(tmp_path):
    """
    Known, accepted limit (DiVoid #10543 §9.2 / §10.1), not a regression: os.path.realpath
    does not resolve hardlinks, because a hardlink has no target -- both names are equally
    real. A hardlink to .git/config under an unlisted name is therefore allowed, and reads
    the credential. Closing this needs volume+file-index comparison, judged disproportionate
    (DiVoid #10543 Q4). Do not "fix" this test by making it expect a refusal.
    """
    root_dir = tmp_path / "workspace"
    git_dir = root_dir / ".git"
    git_dir.mkdir(parents=True)
    (git_dir / "config").write_bytes(b"[remote]")
    innocent = root_dir / "innocent.md"

    os.link(str(git_dir / "config"), str(innocent))

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    resolved = paths.gate(str(innocent))
    assert os.path.samefile(resolved, innocent)
    with open(resolved, "rb") as fh:
        assert fh.read() == b"[remote]"


def test_gate_sensitive_denial_message_names_component_and_forbids_copy_workaround(tmp_path):
    root_dir = tmp_path / "workspace"
    git_dir = root_dir / ".git"
    git_dir.mkdir(parents=True)
    target = git_dir / "config"
    target.write_bytes(b"[remote]")

    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(str(target))

    message = exc_info.value.message
    assert ".git" in message
    assert "do not copy" in message.lower()
    assert "deliberate refusal" in message.lower()


@pytest.mark.parametrize(
    "case_variant_relative",
    [
        os.path.join("sub", ".MCP.JSON"),
        os.path.join("sub", "Settings.JSON"),
        os.path.join("sub", "ID_RSA"),
    ],
)
def test_gate_rejects_case_variant_of_sensitive_component_that_does_not_exist_on_disk(
    tmp_path, case_variant_relative
):
    r"""
    Pins normcase in the sensitive-name comparison (DiVoid #10773 W-1). Neither the
    root nor the tail exists on disk, so os.path.realpath cannot canonicalize the
    caller's case -- this is exactly divoid_download_content's write-target shape,
    where the file being refused does not yet exist. Without normcase folding this
    silently ACCEPTS (measured in #10773: sub\.MCP.JSON, sub\Settings.JSON,
    sub\ID_RSA all resolve with case preserved when the component is absent).
    """
    root_dir = tmp_path / "workspace"
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    candidate = str(root_dir / case_variant_relative)
    assert not os.path.exists(os.path.dirname(candidate)), (
        "the parent directory must not exist on disk, or realpath may canonicalize "
        "case and this test would not isolate the normcase fold"
    )

    with pytest.raises(InvariantViolation) as exc_info:
        paths.gate(candidate)
    assert exc_info.value.code == "path_denied_sensitive"


def test_gate_allows_ordinary_non_sensitive_case_variant_that_does_not_exist_on_disk(tmp_path):
    """The dual of the case-variant pin above: an ordinary in-root path with no
    sensitive component, non-existent on disk, is still accepted -- normcase folding
    the comparison must not turn into folding it into a false denial."""
    root_dir = tmp_path / "workspace"
    paths.init(env={"DIVOID_MCP_FILE_ROOT": str(root_dir)})

    candidate = str(root_dir / "sub" / "NOTES.MD")
    assert not os.path.exists(os.path.dirname(candidate)), (
        "the parent directory must not exist on disk, or this test proves nothing "
        "about the non-existent-tail case"
    )

    resolved = paths.gate(candidate)
    assert os.path.normcase(resolved) == os.path.normcase(candidate)
