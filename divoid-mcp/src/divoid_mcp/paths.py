"""Filesystem-path containment gate: validates a caller-supplied path against a frozen root list."""

from __future__ import annotations

import logging
import os
from typing import Final

from .errors import InvariantViolation

logger = logging.getLogger(__name__)

ENV_VAR: Final[str] = "DIVOID_MCP_FILE_ROOT"

_REJECTED_PREFIXES: Final[tuple[str, ...]] = ("\\\\?\\", "\\\\.\\", "//?/", "//./")

_roots: tuple[str, ...] = ()


def init(env: "os._Environ[str] | dict[str, str] | None" = None) -> None:
    """Establishes the frozen root list for the process lifetime from DIVOID_MCP_FILE_ROOT, or the cwd. Never raises."""
    global _roots

    if env is None:
        env = os.environ

    raw = env.get(ENV_VAR)
    candidates = [p for p in raw.split(os.pathsep) if p] if raw else [os.getcwd()]

    resolved: list[str] = []
    for candidate in candidates:
        try:
            real = os.path.realpath(candidate)
        except Exception as exc:
            logger.warning(
                "%s: root candidate %r could not be resolved (%s) -- discarded.",
                ENV_VAR, candidate, exc,
            )
            continue
        if not _is_usable_root(real):
            logger.warning(
                "%s: root candidate %r resolved to %r, which is a filesystem/drive root "
                "or the user's home directory -- discarded as too broad to be a safe "
                "containment boundary.",
                ENV_VAR, candidate, real,
            )
            continue
        resolved.append(real)

    _roots = tuple(resolved)

    if not _roots:
        logger.warning(
            "No usable filesystem root configured. divoid_download_content and "
            "divoid_set_content(path=...) will return file_root_unusable for every "
            "call until %s is set to a valid %s-separated directory list. "
            "See divoid-mcp/README.md.",
            ENV_VAR, os.pathsep,
        )
    else:
        logger.info("Filesystem path containment roots: %s", _roots)


def _is_usable_root(real: str) -> bool:
    """A resolved root must have a parent (not a filesystem/drive root) and must not be $HOME."""
    parent = os.path.dirname(real)
    if not parent or parent == real:
        return False

    try:
        home_real = os.path.realpath(os.path.expanduser("~"))
    except Exception:
        home_real = os.path.expanduser("~")
    if os.path.normcase(real) == os.path.normcase(home_real):
        return False

    return True


def roots() -> tuple[str, ...]:
    """Return the frozen root tuple established by init(). Empty if unusable or uninitialised."""
    return _roots


def gate(path: str) -> str:
    """Validates a caller-supplied path against the frozen roots and returns the resolved path.

    Raises InvariantViolation with code 'file_root_unusable' or 'path_outside_root'.
    """
    if not _roots:
        raise InvariantViolation(
            "file_root_unusable",
            "The divoid-mcp server has no usable filesystem root configured, so "
            "path-bearing calls are disabled process-wide. This is not something "
            f"you can fix by retrying or choosing a different path -- an operator "
            f"must set {ENV_VAR} to a valid directory (see divoid-mcp/README.md).",
        )

    for prefix in _REJECTED_PREFIXES:
        if path.startswith(prefix):
            raise InvariantViolation(
                "path_outside_root",
                f"Path {path!r} uses the extended-length/device namespace prefix "
                f"{prefix!r}, which is rejected before resolution because it resolves "
                "unpredictably. This is an intentional containment boundary, not a "
                "tool defect -- do not retry, re-encode the path, or fall back to raw "
                f"REST. Active root(s): {', '.join(_roots)}.",
            )

    if "\x00" in path:
        raise InvariantViolation(
            "path_outside_root",
            f"Path {path!r} contains a NUL byte, which os.open cannot handle. "
            "This is an intentional containment boundary, not a tool defect -- "
            "do not retry, re-encode the path, or fall back to raw REST. "
            f"Active root(s): {', '.join(_roots)}.",
        )

    try:
        real = os.path.realpath(path)
    except Exception as exc:
        raise InvariantViolation(
            "path_outside_root",
            f"Path {path!r} could not be resolved ({exc}). This is an intentional "
            "containment boundary, not a tool defect -- do not retry, re-encode the "
            f"path, or fall back to raw REST. Active root(s): {', '.join(_roots)}.",
        ) from exc

    real_folded = os.path.normcase(real)
    for root in _roots:
        root_folded = os.path.normcase(root)
        try:
            common = os.path.commonpath([real_folded, root_folded])
        except ValueError:
            continue
        if common == root_folded:
            return real

    raise InvariantViolation(
        "path_outside_root",
        f"Path {path!r} resolves to {real!r}, which is outside every active root "
        f"({', '.join(_roots)}). This is an intentional containment boundary, not a "
        "tool defect -- do not retry, re-encode the path, or fall back to raw REST. "
        "Choose a path inside one of the active roots instead.",
    )
