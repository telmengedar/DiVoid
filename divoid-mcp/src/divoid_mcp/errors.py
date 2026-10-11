"""
Error types and HTTP-to-MCP error mapping for divoid-mcp.

InvariantViolation is raised by the invariant guard in composite tools
before any HTTP call. The tool dispatcher catches it and returns a
structured MCP error envelope.

error_mapper translates (http_status, response_body) into the same
structured shape. The api key is never included in any output branch.
"""

from __future__ import annotations

import json
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


def backend_error_text(body: bytes) -> str:
    """
    Return the 'text' field of the backend's error envelope.

    Returns an empty string when the body is not JSON, not an object, or has
    no string 'text' field. Never raises.
    """
    try:
        data = json.loads(body.decode("utf-8"))
    except Exception:
        return ""
    if not isinstance(data, dict):
        return ""
    text = data.get("text")
    return text if isinstance(text, str) else ""


def map_http_error(
    status: int,
    body: bytes,
    api_key: str,
    context: str = "",
    not_found_explanation: str | None = None,
) -> tuple[str, str]:
    """
    Translate an HTTP error status + body into (code, message).

    api_key is passed so we can redact it from any body text, as a
    defence-in-depth measure (the primary defence is never putting it there).

    not_found_explanation replaces the default node-oriented sentence on a 404
    when the call site knows its 404 is about something else. The code stays
    node_not_found. Ignored for every other status.

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
            f"{prefix}DiVoid rejected the request as unauthorized (401). The API key may "
            "be invalid or expired. Check the credentials this server was started with -- "
            "DIVOID_MCP_API_KEY in the MCP client's env block, or the fallback credentials "
            "file. The startup log line \"Config loaded: source=...\" names which one was "
            "used.",
        )

    if status == 404:
        explanation = not_found_explanation or (
            "The node does not exist, or the caller's access "
            "does not permit this operation on it (DiVoid answers 404 for per-node access "
            "denials so that non-readers cannot learn a node exists)."
        )
        return ("node_not_found", f"{prefix}DiVoid returned 404. {explanation}")

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
