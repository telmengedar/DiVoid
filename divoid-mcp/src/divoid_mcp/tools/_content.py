"""
Shared body resolution for content-bearing tools: the exclusivity guard and the
containment-gated file read that divoid_set_content and the four composite
create tools (create_documentation, create_task, create_session_log,
create_node) use to turn `content` or `path` into bytes.

Two responsibilities, split the way set_content already split them:
  - guard_exclusive: pure argument check, no I/O -- callable from a tool's
    _check_invariants before any HTTP call.
  - resolve_body: I/O resolution, welded to the containment gate (paths.gate)
    and the open() call -- the read half of the gate/open pair for an
    uploaded body lives only here.

Whether a body is REQUIRED stays each tool's own concern; this module only
turns whichever input was given into bytes, or refuses.
"""

from __future__ import annotations

import logging
from typing import Any

from .. import paths
from ..errors import InvariantViolation, make_error_content

logger = logging.getLogger(__name__)

PATH_DESCRIPTION_CLAUSE = """\
Provide `path` instead of `content` to upload the body from a local file rather \
than retyping it through the model -- this is a fidelity fix, not a token-cost one: \
re-emitting a large body risks silent transcription drift (a dropped table row, a \
normalised dash) that produces a node that looks right and is not (DiVoid #9876). \
Mutually exclusive with `content`; giving both is rejected \
as content_path_conflict before any HTTP call. This tool DOES accept a file \
body -- a refusal names which of two grounds stopped THIS path from being used, \
it does not mean files are unsupported. `path_outside_root` is fixable: choose \
a path inside the server's configured workspace root(s). `path_denied_sensitive` \
is a deliberate, permanent refusal for that path -- there is no retry, no \
re-spelled path, and copying the file to another name and uploading the copy is \
not the remedy. A file that reads as zero bytes is refused (file_empty); a \
whitespace-only file is uploaded as-is, unlike whitespace-only inline `content`, \
which is refused.\
"""


def guard_exclusive(content: str | None, path: str | None) -> None:
    """Raises content_path_conflict when both content and path are given. Pure -- no I/O."""
    if content is not None and path is not None:
        raise InvariantViolation(
            "content_path_conflict",
            "Provide either 'content' or 'path', not both. "
            "They are mutually exclusive ways of supplying the same body.",
        )


def resolve_body(
    content: str | None,
    path: str | None,
    label: str,
) -> tuple[bytes | None, dict[str, Any] | None]:
    """
    Turns whichever of content/path was given into bytes, or a refusal envelope.

    Returns (bytes, None) on success, (None, envelope) on refusal, and
    (None, None) when both are absent -- the caller decides whether that is
    legal. `label` names the calling tool for the log line only.
    """
    if content is not None:
        return content.encode("utf-8"), None

    if path is None:
        return None, None

    if not path.strip():
        logger.info("%s path=%r -> path_empty", label, path)
        return None, {
            "isError": True,
            "content": make_error_content("path_empty", "path must be a non-empty string."),
        }

    try:
        resolved_path = paths.gate(path)
    except InvariantViolation as exc:
        logger.info("%s path=%r -> %s", label, path, exc.code)
        return None, {"isError": True, "content": make_error_content(exc.code, exc.message)}

    try:
        with open(resolved_path, "rb") as fh:
            content_bytes = fh.read()
    except FileNotFoundError:
        logger.info("%s path=%r -> file_not_found", label, path)
        return None, {
            "isError": True,
            "content": make_error_content("file_not_found", f"No such file: {path!r}."),
        }
    except OSError as exc:
        logger.warning("%s path=%r read failed: %s", label, path, exc)
        return None, {
            "isError": True,
            "content": make_error_content("file_read_failed", f"Could not read {path!r}: {exc}"),
        }

    if len(content_bytes) == 0:
        logger.info("%s path=%r -> file_empty", label, path)
        return None, {
            "isError": True,
            "content": make_error_content(
                "file_empty",
                f"{path!r} read as zero bytes. Refusing to upload: an empty body "
                "would replace the node's existing content with nothing "
                "(DiVoid #7878 recorded exactly this incident on node #7872).",
            ),
        }

    return content_bytes, None
