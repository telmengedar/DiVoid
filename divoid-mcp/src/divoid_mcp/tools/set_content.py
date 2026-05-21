"""
divoid_set_content -- primitive wrapper around POST /api/nodes/{id}/content.

Takes content as a JSON string parameter and posts it as UTF-8 bytes with the
specified Content-Type. This eliminates the bash-heredoc UTF-8 mangling bug
class (DiVoid #187) entirely: the content never passes through a shell or a
string-to-data kwarg that could mangle multibyte characters.

The content is encoded to UTF-8 bytes in Python before the HTTP call, matching
the UTF-8-safe path already used by the composite create tools.

Invariant guards (before any HTTP call):
  - content must be non-empty (no whitespace-only posts) → content_empty

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
Post content to a DiVoid node. Accepts content as a plain string parameter and \
uploads it as UTF-8 bytes — this is the safe path that avoids the bash-heredoc \
UTF-8 mangling bug (DiVoid #187). Use this to set or update the body of any node \
that accepts content (task, documentation, session-log, etc.). Content must be \
non-empty (invariant guard: content_empty). The default content_type is \
'text/markdown; charset=utf-8'; override if your content is plain text or another \
format. Returns success confirmation on 2xx.\
"""


def _check_invariants(content: str) -> None:
    """
    Check runtime invariants before making any HTTP call.

    Raises InvariantViolation with a stable code if any invariant is broken.
    The invariant guard is the sole enforcement layer — FastMCP exposes
    content as a plain {"type": "string"} parameter without minLength;
    enforcement is entirely here.
    """
    if not content or not content.strip():
        raise InvariantViolation(
            "content_empty",
            "Content must be non-empty and non-whitespace. "
            "Posting empty or whitespace-only content creates a structurally inert node "
            "(per DiVoid #493 §4). Provide the actual content body.",
        )


async def _execute(
    id: int,
    content: str,
    config: "DivoidConfig",
    content_type: str = _DEFAULT_CONTENT_TYPE,
) -> dict[str, Any]:
    """
    Core implementation of divoid_set_content.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.

    Callers must run _check_invariants() before calling this function.
    """
    content_bytes = content.encode("utf-8")
    logger.info(
        "divoid_set_content id=%d content_type=%r byte_length=%d",
        id, content_type, len(content_bytes),
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
        content: str,
        content_type: str = _DEFAULT_CONTENT_TYPE,
    ) -> dict[str, Any]:
        """
        Post content to a DiVoid node (UTF-8-safe).

        Args:
            id: The node id to set content on (required).
            content: The content body as a string (required, must be non-empty).
                     Markdown is the canonical format. The content is encoded to
                     UTF-8 bytes before posting — this avoids the shell heredoc
                     UTF-8 mangling trap (DiVoid #187). The invariant guard is
                     the sole enforcement layer (FastMCP exposes content as plain
                     string, no minLength in JSON Schema).
            content_type: MIME type for the content. Default is
                          'text/markdown; charset=utf-8'. Override only if your
                          content is not markdown (e.g. 'text/plain; charset=utf-8').
        """
        # --- Invariant guard (before any HTTP call) ---
        try:
            _check_invariants(content)
        except InvariantViolation as exc:
            logger.debug("divoid_set_content invariant violation: %s", exc.code)
            return {"isError": True, "content": make_error_content(exc.code, exc.message)}

        return await _execute(
            id=id,
            content=content,
            config=config,
            content_type=content_type,
        )
