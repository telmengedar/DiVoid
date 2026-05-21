"""
divoid_get_content — fetch the text body of a node, decoded as UTF-8.

Wraps GET /api/nodes/{id}/content. Returns the body verbatim as a string.
Non-text content types produce a structured error rather than binary noise.

Architecture reference: §8.3
"""

from __future__ import annotations

import logging
from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import make_error_content, map_http_error, map_unreachable

logger = logging.getLogger(__name__)

_TOOL_DESCRIPTION = """\
Fetch the content body of a node, decoded as a UTF-8 string. Use this on \
documentation, task, bug, session-log, and chat nodes after you've identified \
them via search or divoid_get_node. Returns the body verbatim (no further \
processing). If the node has no content the result has content="" and \
content_type=null — that is itself a signal: per DiVoid structural conventions \
(#493 §4), content-required types should never be empty.\
"""

# Content-type prefixes considered text (safe to decode as UTF-8).
_TEXT_PREFIXES = (
    "text/",
    "application/json",
    "application/xml",
    "application/javascript",
    "application/typescript",
)


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_get_content(id: int) -> dict[str, Any]:
        """
        Fetch the content body of a node, decoded as a UTF-8 string.

        Args:
            id: The node id whose content body to fetch. Must be a positive integer.
        """
        if id < 1:
            return {
                "isError": True,
                "content": make_error_content("divoid_bad_request", "id must be a positive integer."),
            }

        logger.info("divoid_get_content id=%d", id)

        try:
            result = await http_client.get(f"nodes/{id}/content")
        except http_client.DiVoidUnreachable as exc:
            code, msg = map_unreachable(exc, config.api_key, "divoid_get_content")
            logger.warning("divoid_get_content id=%d err=%s", id, code)
            return {"isError": True, "content": make_error_content(code, msg)}

        # 404 on the content endpoint covers two distinct cases:
        #   (a) Node does not exist → body: {"code":"data_entitynotfound","text":"'Node' with id '...' not found"}
        #   (b) Node exists but has no content → body text contains "has no content"
        # We must distinguish them: (b) is a valid empty-content signal; (a) is a hard error.
        if result.status == 404:
            body_text = ""
            try:
                body_json = result.json()
                body_text = body_json.get("text", "") if isinstance(body_json, dict) else ""
            except Exception:
                # Malformed JSON — can't determine which case; default to node_not_found.
                logger.warning(
                    "divoid_get_content id=%d 404 with unparseable body, treating as node_not_found",
                    id,
                )
                return {
                    "isError": True,
                    "content": make_error_content(
                        "node_not_found",
                        f"Node {id} not found (404 with unparseable body).",
                    ),
                }

            if "has no content" in body_text:
                # Node exists but has no content body — return the empty-content shape.
                logger.info("divoid_get_content id=%d → empty (node has no content)", id)
                return {"id": id, "content": "", "content_type": None, "byte_length": 0}

            # Node truly does not exist.
            logger.info("divoid_get_content id=%d → node_not_found", id)
            return {
                "isError": True,
                "content": make_error_content(
                    "node_not_found",
                    f"Node {id} not found.",
                ),
            }

        if not result.ok:
            code, msg = map_http_error(result.status, result.body, config.api_key, "divoid_get_content")
            logger.info("divoid_get_content id=%d err=%s status=%d", id, code, result.status)
            return {"isError": True, "content": make_error_content(code, msg)}

        content_type = result.headers.get("content-type", "")
        byte_length = len(result.body)

        # Check if the content is text before decoding.
        is_text = not content_type or any(
            content_type.lower().startswith(p) for p in _TEXT_PREFIXES
        )

        if not is_text:
            logger.info(
                "divoid_get_content id=%d content_type=%r is not text; returning error",
                id, content_type,
            )
            return {
                "isError": True,
                "content": make_error_content(
                    "content_not_text",
                    f"Node {id} has non-text content (content-type: {content_type!r}). "
                    f"Byte length: {byte_length}. Use raw HTTP if you need binary content.",
                ),
            }

        try:
            decoded = result.body.decode("utf-8")
        except UnicodeDecodeError:
            logger.warning("divoid_get_content id=%d UTF-8 decode failed, byte_length=%d", id, byte_length)
            return {
                "isError": True,
                "content": make_error_content(
                    "content_decode_failed",
                    f"Node {id} content could not be decoded as UTF-8 despite text content-type "
                    f"({content_type!r}). Byte length: {byte_length}.",
                ),
            }

        logger.info(
            "divoid_get_content id=%d ok content_type=%r byte_length=%d",
            id, content_type, byte_length,
        )
        return {
            "id": id,
            "content": decoded,
            "content_type": content_type or None,
            "byte_length": byte_length,
        }
