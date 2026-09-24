"""
Unit tests for `refinement` / `clear_refinement` on divoid_patch_node.

Mirrors the existing severity / clear_severity contract exactly: refinement is an
open-vocabulary string with no validation; clear_refinement composes a
`{"op": "replace", "path": "/refinement", "value": None}` op, the un-set verb for
a field whose absence is meaningful (per the design's "unset means unclassified"
rule). refinement alone must satisfy the no_fields_to_patch invariant guard --
before this change, a call with only `refinement` set would have been rejected
as a no-op.

No network calls and no DiVoid credentials are required.
"""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.tools.patch_node import register as register_patch_node

_DUMMY_BASE = "http://divoid.test"
_DUMMY_KEY = "dummy-key-for-unit-tests"
_NODE_URL = f"{_DUMMY_BASE}/nodes/{{id}}"


@pytest.fixture(scope="module")
def server() -> FastMCP:
    config = DivoidConfig(base_url=_DUMMY_BASE, api_key=_DUMMY_KEY, source="env")
    http_client.init(_DUMMY_BASE, _DUMMY_KEY)
    mcp_server = FastMCP("divoid-mcp-refinement-patch-test")
    mcp_server.config = config  # type: ignore[attr-defined]
    register_patch_node(mcp_server)
    return mcp_server


async def _call(server: FastMCP, args: dict[str, Any]) -> dict[str, Any]:
    result = await server._tool_manager.call_tool("divoid_patch_node", args)
    assert isinstance(result, dict), f"Expected dict, got {type(result)}"
    return result


@pytest.mark.asyncio
async def test_refinement_alone_satisfies_no_fields_to_patch_guard(server: FastMCP) -> None:
    """A patch with ONLY refinement set must not be rejected as a no-op.

    Substitution probe: remove `has_refinement_op` from the no_fields_to_patch
    condition in patch_node._check_invariants -- this call would incorrectly
    raise no_fields_to_patch, and this test would fail on the isError assertion.
    """
    node_id = 1
    with respx.mock(assert_all_called=False) as mock:
        mock.patch(_NODE_URL.format(id=node_id)).mock(
            return_value=httpx.Response(200, json={"id": node_id, "refinement": "ready"})
        )
        result = await _call(server, {"id": node_id, "refinement": "ready"})

    assert result.get("isError") is not True, (
        f"Expected success (refinement alone is a valid patch), got: {result}"
    )


@pytest.mark.asyncio
async def test_refinement_replace_op_composed(server: FastMCP) -> None:
    """refinement='ready' -> JSON-Patch body has replace /refinement op with that value.

    Substitution probe: remove the `if refinement is not None: ops.append(...)`
    branch from patch_node._execute -- the /refinement op is absent and this
    test fails on the length assertion.
    """
    node_id = 2
    captured_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_body.append(json.loads(request.content))
            return httpx.Response(200, json={"id": node_id, "refinement": "ready"})

        mock.patch(_NODE_URL.format(id=node_id)).mock(side_effect=capture)
        result = await _call(server, {"id": node_id, "refinement": "ready"})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    ops = captured_body[0]
    refinement_ops = [op for op in ops if op.get("path") == "/refinement"]
    assert len(refinement_ops) == 1, f"Expected exactly one /refinement op, got ops: {ops!r}"
    assert refinement_ops[0]["op"] == "replace"
    assert refinement_ops[0]["value"] == "ready"


@pytest.mark.asyncio
async def test_clear_refinement_composes_replace_with_null(server: FastMCP) -> None:
    """clear_refinement=True -> replace /refinement op with value=None (not omitted, not "").

    Substitution probe: remove the `elif clear_refinement:` branch -- no
    /refinement op is sent at all, and this test fails on the length assertion.
    """
    node_id = 3
    captured_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_body.append(json.loads(request.content))
            return httpx.Response(200, json={"id": node_id, "refinement": None})

        mock.patch(_NODE_URL.format(id=node_id)).mock(side_effect=capture)
        result = await _call(server, {"id": node_id, "clear_refinement": True})

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    ops = captured_body[0]
    refinement_ops = [op for op in ops if op.get("path") == "/refinement"]
    assert len(refinement_ops) == 1, f"Expected exactly one /refinement op, got ops: {ops!r}"
    assert refinement_ops[0]["op"] == "replace"
    assert refinement_ops[0]["value"] is None, (
        f"Expected value=None (JSON null) for clear_refinement, got: {refinement_ops[0]['value']!r}"
    )


@pytest.mark.asyncio
async def test_explicit_refinement_value_wins_over_clear_refinement(server: FastMCP) -> None:
    """Both refinement and clear_refinement set -> the explicit value wins (severity precedent)."""
    node_id = 4
    captured_body: list[Any] = []

    with respx.mock(assert_all_called=False) as mock:
        def capture(request: httpx.Request) -> httpx.Response:
            captured_body.append(json.loads(request.content))
            return httpx.Response(200, json={"id": node_id, "refinement": "ready"})

        mock.patch(_NODE_URL.format(id=node_id)).mock(side_effect=capture)
        result = await _call(server, {
            "id": node_id, "refinement": "ready", "clear_refinement": True,
        })

    assert result.get("isError") is not True, f"Expected success, got: {result}"
    ops = captured_body[0]
    refinement_ops = [op for op in ops if op.get("path") == "/refinement"]
    assert len(refinement_ops) == 1, f"Expected exactly one /refinement op, got ops: {ops!r}"
    assert refinement_ops[0]["value"] == "ready"


@pytest.mark.asyncio
async def test_no_fields_to_patch_still_rejects_truly_empty_call(server: FastMCP) -> None:
    """Regression: adding refinement must not accidentally widen the no-op guard."""
    result = await _call(server, {"id": 5})

    assert result.get("isError") is True, "Expected isError=True for a genuinely empty patch"
    content_text: str = result["content"][0]["text"]
    assert "no_fields_to_patch" in content_text


@pytest.mark.asyncio
async def test_refinement_against_pre_refinement_backend_surfaces_readable_error(
    server: FastMCP,
) -> None:
    """Version skew: a backend that predates the refinement column rejects the PATCH
    with 400 'Property not found' rather than silently dropping the field. This test
    pins that the existing generic error-mapping path (used for every PATCH, not
    written for refinement specifically) surfaces that message to the caller rather
    than swallowing it -- i.e. divoid_patch_node against an old backend fails loud,
    not silent, with no refinement-specific code needed to make that true.
    """
    node_id = 6
    body = json.dumps({
        "code": "badparameter",
        "text": "Property 'refinement' not found on 'Node'",
    }).encode()

    with respx.mock(assert_all_called=False) as mock:
        mock.patch(_NODE_URL.format(id=node_id)).mock(
            return_value=httpx.Response(400, content=body)
        )
        result = await _call(server, {"id": node_id, "refinement": "ready"})

    assert result.get("isError") is True, (
        f"Expected isError=True when the backend rejects an unrecognised property, got: {result}"
    )
    content_text: str = result["content"][0]["text"]
    assert "refinement" in content_text, (
        f"Expected the backend's own error text to reach the caller, got: {content_text!r}"
    )
