"""
divoid_download_content — fetch a node's content and write the raw bytes to disk.

Wraps GET /api/nodes/{id}/content, writes result.body verbatim (no decoding,
no transform) to the given path. Mimetype-agnostic: images, PDFs, binaries, and
text all round-trip byte-identical.

Parent directories are created automatically (os.makedirs exist_ok=True) so
callers can specify a target path without pre-creating the directory tree.

Architecture reference: DiVoid task #6597.
"""

from __future__ import annotations

import logging
import os
from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import make_error_content, map_http_error, map_unreachable

logger = logging.getLogger(__name__)

_TOOL_DESCRIPTION = """\
GET a node's content and write the raw bytes to a local file at `path`. \
Mimetype-agnostic: images, PDFs, and binary blobs round-trip byte-identical. \
Parent directories are created automatically. Returns \
{success, path, bytes_written, content_type}; does NOT return the content itself. \
Use divoid_get_content for text nodes where you want the body inline.\
"""


async def _execute(node_id: int, path: str, config: DivoidConfig) -> dict[str, Any]:
    """
    Core implementation — separated from the MCP decorator so smoke tests can call it directly.

    Returns a plain dict. On success: {success, path, bytes_written, content_type}.
    On failure: {isError, content}.
    """
    if node_id < 1:
        return {
            "isError": True,
            "content": make_error_content(
                "divoid_bad_request", "node_id must be a positive integer."
            ),
        }
    if not path or not path.strip():
        return {
            "isError": True,
            "content": make_error_content("divoid_bad_request", "path must be a non-empty string."),
        }

    logger.info("divoid_download_content node_id=%d path=%r", node_id, path)

    try:
        result = await http_client.get(f"nodes/{node_id}/content")
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, "divoid_download_content")
        logger.warning("divoid_download_content node_id=%d err=%s", node_id, code)
        return {"isError": True, "content": make_error_content(code, msg)}

    if result.status == 404:
        body_text = ""
        try:
            body_json = result.json()
            body_text = body_json.get("text", "") if isinstance(body_json, dict) else ""
        except Exception:
            logger.warning(
                "divoid_download_content node_id=%d 404 with unparseable body",
                node_id,
            )
            return {
                "isError": True,
                "content": make_error_content(
                    "node_not_found",
                    f"Node {node_id} not found (404 with unparseable body).",
                ),
            }

        if "has no content" in body_text:
            logger.info("divoid_download_content node_id=%d -> node has no content", node_id)
            return {
                "isError": True,
                "content": make_error_content(
                    "node_has_no_content",
                    f"Node {node_id} exists but has no content to download.",
                ),
            }

        logger.info("divoid_download_content node_id=%d -> node_not_found", node_id)
        return {
            "isError": True,
            "content": make_error_content("node_not_found", f"Node {node_id} not found."),
        }

    if not result.ok:
        code, msg = map_http_error(
            result.status, result.body, config.api_key, "divoid_download_content"
        )
        logger.info(
            "divoid_download_content node_id=%d err=%s status=%d", node_id, code, result.status
        )
        return {"isError": True, "content": make_error_content(code, msg)}

    raw_bytes = result.body
    content_type = result.headers.get("content-type", None)

    parent = os.path.dirname(os.path.abspath(path))
    os.makedirs(parent, exist_ok=True)

    try:
        with open(path, "wb") as fh:
            fh.write(raw_bytes)
    except OSError as exc:
        logger.warning("divoid_download_content node_id=%d write failed: %s", node_id, exc)
        return {
            "isError": True,
            "content": make_error_content(
                "write_failed",
                f"Could not write to {path!r}: {exc}",
            ),
        }

    bytes_written = len(raw_bytes)
    logger.info(
        "divoid_download_content node_id=%d ok bytes_written=%d path=%r content_type=%r",
        node_id, bytes_written, path, content_type,
    )
    return {
        "success": True,
        "path": path,
        "bytes_written": bytes_written,
        "content_type": content_type,
    }


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_download_content(node_id: int, path: str) -> dict[str, Any]:
        """
        Fetch a node's content and write the raw bytes to a local file.

        Args:
            node_id: The node whose content to download. Must be a positive integer.
            path:    Absolute or relative path to write the file to. Parent directories
                     are created automatically. The file is written in binary mode;
                     no encoding or decoding is applied.
        """
        return await _execute(node_id=node_id, path=path, config=config)
