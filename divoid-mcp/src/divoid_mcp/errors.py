"""
Error types and HTTP-to-MCP error mapping for divoid-mcp.

InvariantViolation is raised by the invariant guard (Phase 2 composites)
before any HTTP call. The tool dispatcher catches it and returns a
structured MCP error envelope.

error_mapper translates (http_status, response_body) into the same
structured shape. The api key is never included in any output branch.
"""

from __future__ import annotations

import logging
from typing import Any

logger = logging.getLogger(__name__)


class InvariantViolation(Exception):
    """
    Raised when a tool call violates a DiVoid structural invariant.

    code:    stable machine-readable code (e.g. 'content_required')
    message: human-readable explanation with actionable hint
    """

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


def make_error_content(code: str, message: str) -> list[dict[str, Any]]:
    """Return the MCP 'content' array for an error response."""
    return [{"type": "text", "text": f"{code}: {message}"}]


def map_http_error(
    status: int,
    body: bytes,
    api_key: str,
    context: str = "",
) -> tuple[str, str]:
    """
    Translate an HTTP error status + body into (code, message).

    api_key is passed so we can redact it from any body text, as a
    defence-in-depth measure (the primary defence is never putting it there).

    Returns (code, human_readable_message).
    """
    # Decode body for the message; strip the api key just in case.
    try:
        body_text = body.decode("utf-8", errors="replace")
    except Exception:
        body_text = repr(body[:200])

    body_text = _redact(body_text, api_key)

    prefix = f"{context}: " if context else ""

    if status == 401:
        return (
            "divoid_unauthorized",
            f"{prefix}DiVoid rejected the request as unauthorized (401). "
            "The API key may be invalid or expired. Check ~/.claude/secrets/.divoid-online.",
        )

    if status == 404:
        return (
            "node_not_found",
            f"{prefix}DiVoid returned 404. The node does not exist.",
        )

    if status >= 500:
        return (
            "divoid_server_error",
            f"{prefix}DiVoid returned a server error ({status}). Body: {body_text[:200]}",
        )

    # 4xx other than 401/404
    return (
        "divoid_bad_request",
        f"{prefix}DiVoid returned {status}. Body: {body_text[:400]}",
    )


def map_unreachable(exc: Exception, api_key: str, context: str = "") -> tuple[str, str]:
    """Translate a network/timeout exception into (code, message)."""
    msg = _redact(str(exc), api_key)
    prefix = f"{context}: " if context else ""
    return (
        "divoid_unreachable",
        f"{prefix}Could not reach DiVoid: {msg}",
    )


def _redact(text: str, api_key: str) -> str:
    """Remove any occurrence of the api key from text."""
    if api_key and api_key in text:
        text = text.replace(api_key, "***")
    return text
