"""
divoid_set_content -- primitive wrapper around POST /api/nodes/{id}/content.

Takes content either inline (`content`, a string encoded to UTF-8 bytes before
posting) or from a local file (`path`, read as raw bytes and posted verbatim).
Exactly one of the two must be given. The inline path eliminates the
bash-heredoc UTF-8 mangling bug class (DiVoid #187); the file path eliminates
the LLM-transcription-drift risk of re-emitting a large body through the
model's output channel (DiVoid #8523 Defect 2, #7895 Finding 1).

Either way the bytes reach http_client.post_bytes unchanged — no decode/
re-encode step happens between reading the file and posting it, so binary or
non-UTF-8 files round-trip byte-identical.

Invariant guards (before any HTTP call):
  - exactly one of content / path must be given -> content_path_conflict / content_path_required
  - inline content must be non-empty (no whitespace-only posts) -> content_empty
  - path must be a non-empty string -> path_empty

File-read outcomes (resolved after the guard, still before the HTTP call):
  - missing file -> file_not_found
  - unreadable file (permissions, path is a directory, etc.) -> file_read_failed
  - file reads as zero bytes -> file_empty (DiVoid #7878: a zero-byte upload
    wiped node #7872's content in a real incident)

Architecture reference: DiVoid #695 §Tool 3 (set_content primitive).
API reference: DiVoid #8 (POST /api/nodes/{id}/content).
"""

from __future__ import annotations

import logging
from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import InvariantViolation, make_error_content, map_http_error, map_unreachable

logger = logging.getLogger(__name__)

_DEFAULT_CONTENT_TYPE = "text/markdown; charset=utf-8"

_TOOL_DESCRIPTION = """\
Post content to a DiVoid node. Provide EXACTLY ONE of `content` (a plain string \
uploaded as UTF-8 bytes) or `path` (a local file whose bytes are read and \
uploaded verbatim). Use `path` for large bodies instead of inlining them via \
`content` — it avoids re-emitting the body through the model's output channel \
and the transcription drift that causes. Both paths avoid the bash-heredoc \
UTF-8 mangling bug (DiVoid #187). Use this to set or update the body of any node \
that accepts content (task, documentation, session-log, etc.). Neither `content` \
nor a file read via `path` may be empty. The default content_type is \
'text/markdown; charset=utf-8'; override if your content is plain text, binary, \
or another format — it is not inferred from a file extension. Returns success \
confirmation on 2xx.\
"""


def _check_invariants(content: str | None, path: str | None) -> None:
    """
    Check runtime invariants before making any HTTP call.

    Raises InvariantViolation with a stable code if any invariant is broken.
    File-read outcomes (missing/unreadable/empty file) are resolved separately
    in _execute, since they require I/O the pure argument check here does not
    perform.
    """
    if content is not None and path is not None:
        raise InvariantViolation(
            "content_path_conflict",
            "Provide either 'content' or 'path', not both. "
            "They are mutually exclusive ways of supplying the same body.",
        )
    if content is None and path is None:
        raise InvariantViolation(
            "content_path_required",
            "Provide exactly one of 'content' (inline string) or 'path' "
            "(local file to read and upload).",
        )
    if content is not None:
        if not content.strip():
            raise InvariantViolation(
                "content_empty",
                "Content must be non-empty and non-whitespace. "
                "Posting empty or whitespace-only content creates a structurally inert node "
                "(per DiVoid #493 §4). Provide the actual content body.",
            )
    elif not path.strip():
        raise InvariantViolation(
            "path_empty",
            "path must be a non-empty string.",
        )


async def _execute(
    id: int,
    config: "DivoidConfig",
    content: str | None = None,
    path: str | None = None,
    content_type: str = _DEFAULT_CONTENT_TYPE,
) -> dict[str, Any]:
    """
    Core implementation of divoid_set_content.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.

    Callers must run _check_invariants() before calling this function.
    """
    if path is not None:
        try:
            with open(path, "rb") as fh:
                content_bytes = fh.read()
        except FileNotFoundError:
            logger.info("divoid_set_content id=%d path=%r -> file_not_found", id, path)
            return {
                "isError": True,
                "content": make_error_content(
                    "file_not_found", f"No such file: {path!r}."
                ),
            }
        except OSError as exc:
            logger.warning("divoid_set_content id=%d path=%r read failed: %s", id, path, exc)
            return {
                "isError": True,
                "content": make_error_content(
                    "file_read_failed", f"Could not read {path!r}: {exc}"
                ),
            }

        if len(content_bytes) == 0:
            logger.info("divoid_set_content id=%d path=%r -> file_empty", id, path)
            return {
                "isError": True,
                "content": make_error_content(
                    "file_empty",
                    f"{path!r} read as zero bytes. Refusing to upload: an empty body "
                    "would replace the node's existing content with nothing "
                    "(DiVoid #7878 recorded exactly this incident on node #7872).",
                ),
            }
    else:
        content_bytes = content.encode("utf-8")

    logger.info(
        "divoid_set_content id=%d source=%s content_type=%r byte_length=%d",
        id, "path" if path is not None else "content", content_type, len(content_bytes),
    )

    try:
        result = await http_client.post_bytes(
            f"nodes/{id}/content",
            content_bytes,
            content_type,
        )
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, f"POST content for node #{id}")
        logger.warning("divoid_set_content id=%d err=%s", id, code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if not result.ok:
        code, msg = map_http_error(
            result.status, result.body, config.api_key,
            f"POST content for node #{id}",
        )
        logger.info("divoid_set_content id=%d err=%s status=%d", id, code, result.status)
        return {"isError": True, "content": make_error_content(code, msg)}

    logger.info("divoid_set_content id=%d ok byte_length=%d", id, len(content_bytes))
    return {
        "id": id,
        "content_type": content_type,
        "content_length": len(content_bytes),
    }


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_set_content(
        id: int,
        content: str | None = None,
        path: str | None = None,
        content_type: str = _DEFAULT_CONTENT_TYPE,
    ) -> dict[str, Any]:
        """
        Post content to a DiVoid node (UTF-8-safe), inline or from a local file.

        Args:
            id: The node id to set content on (required).
            content: The content body as a string. Mutually exclusive with `path` —
                     provide exactly one. Markdown is the canonical format. Encoded
                     to UTF-8 bytes before posting — this avoids the shell heredoc
                     UTF-8 mangling trap (DiVoid #187).
            path: Local file path to read the content from. Mutually exclusive with
                  `content`. The file's bytes are read and posted verbatim — no
                  decode/re-encode step, so binary or non-UTF-8 files round-trip
                  byte-identical. Use this instead of `content` for large bodies.
            content_type: MIME type for the content. Default is
                          'text/markdown; charset=utf-8'. Override only if your
                          content is not markdown (e.g. 'text/plain; charset=utf-8').
                          Not inferred from a file extension when using `path`.
        """
        try:
            _check_invariants(content, path)
        except InvariantViolation as exc:
            logger.debug("divoid_set_content invariant violation: %s", exc.code)
            return {"isError": True, "content": make_error_content(exc.code, exc.message)}

        return await _execute(
            id=id,
            config=config,
            content=content,
            path=path,
            content_type=content_type,
        )
