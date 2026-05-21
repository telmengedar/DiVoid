"""
divoid_send_message -- composite: resolve recipient + POST /api/messages.

Two-step atomic operation from the caller's perspective:
  1. (optional) GET /api/nodes/{recipient_node_id}/user  -- resolve node -> user_id
  2. POST /api/messages                                   -- send the message

Step 1 is skipped when recipient_user_id is supplied directly.

Per DiVoid #435 (Hivemind Protocol messaging), the intended recipient is always
an AGENT's user identity — not a human's. The canonical recipient in this
deployment is Selene (node #11, user_id=2). Self-messaging (authorId==recipientId)
is the dominant pattern: a session of the main agent sends a message to itself so
a different project-scoped session picks it up on its next inbox scan.

Partial failure semantics: if step 1 succeeds but step 2 fails, the error
envelope names the resolved user_id so the caller can retry POST without
re-resolving.

Invariant guard (runtime enforcement, not JSON Schema — FastMCP exposes these as
plain string/integer fields):
  - Exactly one of recipient_node_id or recipient_user_id must be supplied.
  - subject must be non-empty and non-whitespace.
  - body must be non-empty and non-whitespace.
"""

from __future__ import annotations

import logging
from typing import Any

import mcp.server.fastmcp as fastmcp

from .. import http_client
from ..config import DivoidConfig
from ..errors import InvariantViolation, make_error_content, map_http_error, map_unreachable

logger = logging.getLogger(__name__)

_TOOL_DESCRIPTION = """\
Send a message to an agent in the DiVoid hivemind. Atomically resolves the \
recipient (if given as a node-id) and POSTs to /api/messages. \
Recipients are agents, not humans — use Selene's node #11 (user_id=2) as \
the canonical recipient for DiVoid-project sessions (per DiVoid #435). \
Self-messaging (sending from Selene to Selene) is the dominant pattern: \
a DiVoid-backend session sends to itself so a different project-scoped session \
picks it up at its next inbox scan. Never address the human (user_id=1) — \
Toni does not poll the inbox and the message will sit as an orphan. \
Supply recipient_node_id to resolve the user-id automatically (graph-friendly), \
or recipient_user_id to skip resolution (use when you already know it). \
Subject convention: '[<ProjectName>] short punchline' so the receiving session \
can gate by project scope (DiVoid #435 scope discipline).\
"""


def _check_invariants(
    subject: str,
    body: str,
    recipient_node_id: int | None,
    recipient_user_id: int | None,
) -> None:
    """
    Check runtime invariants before making any HTTP call.

    Raises InvariantViolation with a stable code if any invariant is broken.
    The invariant guard is the sole enforcement layer for these constraints —
    FastMCP exposes the parameters as plain types in the JSON Schema without
    oneOf or minLength; enforcement is entirely here.
    """
    # Exactly one recipient must be specified.
    if recipient_node_id is not None and recipient_user_id is not None:
        raise InvariantViolation(
            "mutually_exclusive_recipient",
            "Provide either recipient_node_id or recipient_user_id, not both. "
            "recipient_node_id resolves to a user-id via GET /api/nodes/{id}/user; "
            "recipient_user_id is used directly (use when you already know it).",
        )
    if recipient_node_id is None and recipient_user_id is None:
        raise InvariantViolation(
            "no_recipient",
            "Either recipient_node_id or recipient_user_id is required. "
            "For most cases, use recipient_node_id=11 (Selene's agent node, "
            "resolves to user_id=2). Never use user_id=1 (Toni, the human).",
        )

    # Subject must be non-empty and non-whitespace.
    if not subject or not subject.strip():
        raise InvariantViolation(
            "subject_empty",
            "subject must be non-empty and non-whitespace. "
            "Convention: '[<ProjectName>] short punchline' (DiVoid #435 scope discipline).",
        )

    # Body must be non-empty and non-whitespace.
    if not body or not body.strip():
        raise InvariantViolation(
            "body_empty",
            "body must be non-empty and non-whitespace. "
            "Describe what happened, what the recipient needs to do, and any node references.",
        )


async def _execute(
    subject: str,
    body: str,
    config: "DivoidConfig",
    recipient_node_id: int | None = None,
    recipient_user_id: int | None = None,
) -> dict[str, Any]:
    """
    Core implementation of divoid_send_message.

    Extracted from register() so smoke tests can call it directly — if this
    function is deleted or broken, the smoke test will fail rather than pass
    vacuously.

    Callers must run _check_invariants() before calling this function.
    """
    logger.info(
        "divoid_send_message recipient_node_id=%s recipient_user_id=%s subject=%r",
        recipient_node_id, recipient_user_id, subject[:60],
    )

    # --- Step 1: Resolve recipient node-id to user-id (if needed) ---
    resolved_user_id: int
    if recipient_node_id is not None:
        try:
            resolve_result = await http_client.get(f"nodes/{recipient_node_id}/user")
        except http_client.DiVoidUnreachable as exc:
            code, msg = map_unreachable(
                exc, config.api_key, f"resolve user for node #{recipient_node_id}"
            )
            logger.warning(
                "divoid_send_message step=resolve_user node_id=%d err=%s",
                recipient_node_id, code,
            )
            return {"isError": True, "content": make_error_content(code, msg)}

        if not resolve_result.ok:
            code, msg = map_http_error(
                resolve_result.status,
                resolve_result.body,
                config.api_key,
                f"resolve user for node #{recipient_node_id}",
            )
            logger.info(
                "divoid_send_message step=resolve_user node_id=%d err=%s status=%d",
                recipient_node_id, code, resolve_result.status,
            )
            return {"isError": True, "content": make_error_content(code, msg)}

        try:
            resolved_user_id = resolve_result.json()["userId"]
        except Exception as exc:
            return {
                "isError": True,
                "content": make_error_content(
                    "divoid_bad_request",
                    f"resolve user for node #{recipient_node_id}: "
                    f"Could not parse response: {exc}",
                ),
            }

        logger.info(
            "divoid_send_message recipient_node_id=%d resolved to user_id=%d",
            recipient_node_id, resolved_user_id,
        )
    else:
        resolved_user_id = recipient_user_id  # type: ignore[assignment]

    # --- Step 2: POST /api/messages ---
    message_body = {
        "recipientId": resolved_user_id,
        "subject": subject,
        "body": body,
    }

    try:
        post_result = await http_client.post_json("messages", message_body)
    except http_client.DiVoidUnreachable as exc:
        code, msg = map_unreachable(exc, config.api_key, "send message")
        logger.warning("divoid_send_message step=post_message err=%s", code)
        if recipient_node_id is not None:
            # Resolution succeeded but POST failed — name the resolved user_id
            # so the caller can retry without re-resolving.
            return {
                "isError": True,
                "content": make_error_content(
                    code,
                    f"{msg}. Resolution succeeded: recipient_node_id={recipient_node_id} "
                    f"-> user_id={resolved_user_id}. Retry POST directly with "
                    f"recipient_user_id={resolved_user_id}.",
                ),
            }
        return {"isError": True, "content": make_error_content(code, msg)}

    if not post_result.ok:
        code, msg = map_http_error(
            post_result.status, post_result.body, config.api_key, "send message"
        )
        logger.info(
            "divoid_send_message step=post_message err=%s status=%d",
            code, post_result.status,
        )
        if recipient_node_id is not None:
            return {
                "isError": True,
                "content": make_error_content(
                    "partial_state",
                    f"Resolution succeeded (node #{recipient_node_id} -> user_id={resolved_user_id}) "
                    f"but POST /api/messages failed ({post_result.status}): {msg}. "
                    f"Retry with recipient_user_id={resolved_user_id}.",
                ),
            }
        return {"isError": True, "content": make_error_content(code, msg)}

    try:
        message_data = post_result.json()
        message_id: int = message_data["id"]
        sent_at: str = message_data.get("createdAt", "")
    except Exception as exc:
        return {
            "isError": True,
            "content": make_error_content(
                "divoid_bad_request",
                f"send message: Could not parse response: {exc}",
            ),
        }

    logger.info(
        "divoid_send_message message_id=%d recipient_user_id=%d sent",
        message_id, resolved_user_id,
    )

    result: dict[str, Any] = {
        "message_id": message_id,
        "recipient_user_id": resolved_user_id,
        "subject": subject,
        "sent_at": sent_at,
    }
    if recipient_node_id is not None:
        result["recipient_node_id"] = recipient_node_id

    return result


def register(mcp_server: fastmcp.FastMCP) -> None:
    config: DivoidConfig = mcp_server.config  # type: ignore[attr-defined]

    @mcp_server.tool(description=_TOOL_DESCRIPTION)
    async def divoid_send_message(
        subject: str,
        body: str,
        recipient_node_id: int | None = None,
        recipient_user_id: int | None = None,
    ) -> dict[str, Any]:
        """
        Send a message to an agent in the DiVoid hivemind.

        Args:
            subject: Message subject (required, non-empty). Convention per DiVoid #435:
                     '[<ProjectName>] short punchline' so the receiving session can
                     gate by project scope. Enforcement is by the invariant guard —
                     FastMCP exposes subject as plain string, not minLength.
            body: Message body, markdown (required, non-empty). Describe what happened,
                  what the recipient needs to do, and reference relevant node ids.
                  Enforcement is by the invariant guard — FastMCP exposes body as
                  plain string, not minLength.
            recipient_node_id: Graph node-id of the recipient agent or person.
                               The tool resolves this to a user-id via
                               GET /api/nodes/{id}/user before sending.
                               Use 11 for Selene (main agent, user_id=2).
                               Mutually exclusive with recipient_user_id
                               (invariant guard).
            recipient_user_id: Direct user-id of the recipient. Use when you already
                               know it (e.g. 2 for Selene) to skip the resolution step.
                               Mutually exclusive with recipient_node_id
                               (invariant guard).
        """
        try:
            _check_invariants(
                subject=subject,
                body=body,
                recipient_node_id=recipient_node_id,
                recipient_user_id=recipient_user_id,
            )
        except InvariantViolation as exc:
            logger.debug("divoid_send_message invariant violation: %s", exc.code)
            return {"isError": True, "content": make_error_content(exc.code, exc.message)}

        return await _execute(
            subject=subject,
            body=body,
            config=config,
            recipient_node_id=recipient_node_id,
            recipient_user_id=recipient_user_id,
        )
