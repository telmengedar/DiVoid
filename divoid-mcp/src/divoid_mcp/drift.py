"""
Startup drift canary for divoid-mcp.

On startup, fetches /api/health and the content of node #8 (the API
reference), computes a SHA-256, and compares it against the pinned
constant in version.py.

Behaviour:
- Health unreachable → logs INFO "drift check skipped: API unreachable"
  (not WARN — the server will surface errors when tools are called)
- Hash matches → logs INFO "API reference hash OK"
- Hash mismatch → logs WARNING with both hashes; does NOT block startup

Rationale: refusing to start on drift would be catastrophically obstructive
(a benign content edit on node #8 takes every agent offline). Logging a
visible warning is the right trade-off for Phase 1's single-operator
environment. See architecture §9.4.
"""

from __future__ import annotations

import hashlib
import logging

from . import http_client
from .version import PINNED_API_REF_HASH

logger = logging.getLogger(__name__)

API_REF_NODE_ID = 8


async def run_canary() -> None:
    """
    Run the startup drift check. Never raises; always logs the outcome.
    """
    # Step 1: health check
    try:
        result = await http_client.get("health")
    except http_client.DiVoidUnreachable as exc:
        logger.info(
            "Drift check skipped: DiVoid unreachable on startup (%s). "
            "Tools will return errors if DiVoid stays down.",
            exc,
        )
        return
    except Exception as exc:
        logger.info("Drift check skipped: unexpected error on health check: %s", exc)
        return

    if not result.ok:
        logger.info(
            "Drift check skipped: /api/health returned %d. "
            "Tools will return errors until DiVoid is healthy.",
            result.status,
        )
        return

    # Step 2: fetch node #8 content and hash it
    try:
        content_result = await http_client.get(f"nodes/{API_REF_NODE_ID}/content")
    except http_client.DiVoidUnreachable as exc:
        logger.info("Drift check skipped: could not fetch node #%d: %s", API_REF_NODE_ID, exc)
        return
    except Exception as exc:
        logger.info("Drift check skipped: unexpected error fetching node #%d: %s", API_REF_NODE_ID, exc)
        return

    if not content_result.ok:
        logger.info(
            "Drift check skipped: node #%d returned HTTP %d.",
            API_REF_NODE_ID,
            content_result.status,
        )
        return

    observed_hash = hashlib.sha256(content_result.body).hexdigest()

    if observed_hash == PINNED_API_REF_HASH:
        logger.info("API reference hash OK (node #%d, hash=%s...)", API_REF_NODE_ID, observed_hash[:16])
    else:
        logger.warning(
            "API reference hash MISMATCH — node #%d content has changed since this "
            "version of divoid-mcp was pinned. pinned=%s observed=%s. "
            "The server will continue, but tool behaviour may be incorrect. "
            "See docs/drift-policy.md to update the pin.",
            API_REF_NODE_ID,
            PINNED_API_REF_HASH[:16],
            observed_hash[:16],
        )
