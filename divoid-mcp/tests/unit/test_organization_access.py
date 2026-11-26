"""Organization access compatibility through the existing MCP tools."""

from __future__ import annotations

import json

import httpx
import pytest
import respx
from mcp.server.fastmcp import FastMCP

from divoid_mcp import http_client
from divoid_mcp.config import DivoidConfig
from divoid_mcp.errors import InvariantViolation
from divoid_mcp.tools import (
    create_documentation,
    create_node,
    create_session_log,
    create_task,
    get_node,
    patch_node,
)

_BASE = "http://divoid.test"
_MISSING = object()
_CREATORS = (
    "divoid_create_node",
    "divoid_create_task",
    "divoid_create_documentation",
    "divoid_create_session_log",
)


@pytest.fixture
def server() -> FastMCP:
    http_client.init(_BASE, "dummy-unit-key")
    server = FastMCP("organization-access-test")
    server.config = DivoidConfig(  # type: ignore[attr-defined]
        base_url=_BASE, api_key="dummy-unit-key", source="env",
    )
    for module in (patch_node, get_node, create_node, create_task,
                   create_documentation, create_session_log):
        module.register(server)
    return server


@pytest.mark.parametrize("mask", range(16))
def test_all_known_flag_combinations(mask: int) -> None:
    names = ("Read", "Write", "OrgRead", "OrgWrite")
    tokens = [name for bit, name in enumerate(names) if mask & (1 << bit)]
    assert patch_node._canonicalize_access(mask) == mask
    for separator in (", ", "|"):
        assert patch_node._canonicalize_access(separator.join(tokens) or "None") == mask


@pytest.mark.parametrize("mask", [16, 31, 1 << 30, (1 << 30) | 12, -1])
def test_integer_masks_remain_verbatim(mask: int) -> None:
    assert patch_node._canonicalize_access(mask) == mask


@pytest.mark.parametrize("value", ["", "Unknown", "OrgRead,Unknown", "Read,", "|Write", "16"])
def test_invalid_symbolic_access_is_rejected(value: str) -> None:
    with pytest.raises(InvariantViolation) as exc:
        patch_node._canonicalize_access(value)
    assert exc.value.code == "access_invalid_value"


@pytest.mark.parametrize("tool", _CREATORS)
@pytest.mark.parametrize("access, expected", [
    ("None", 0), ("Read, Write", 3), ("OrgRead|OrgWrite", 12),
    ("Read, OrgWrite", 9), (31, 31), (_MISSING, _MISSING),
])
async def test_creators_forward_access_without_assigning_organization(
    server: FastMCP, tool: str, access: object, expected: object,
) -> None:
    args = {"name": "Compatibility fixture", "content": "body"}
    if tool != "divoid_create_node":
        args["project_id"] = 3
    if access is not _MISSING:
        args["access"] = access

    with respx.mock(assert_all_called=False) as mock:
        mock.get(f"{_BASE}/nodes").respond(200, json={"result": [{"id": 7}]})
        create = mock.post(f"{_BASE}/nodes").respond(200, json={"id": 99, "organizationId": 42})
        mock.post(f"{_BASE}/nodes/99/content").respond(200, json={})
        mock.post(f"{_BASE}/nodes/99/links").respond(200, json={})
        result = await server._tool_manager.call_tool(tool, args)

    assert result.get("isError") is not True
    assert create.call_count == 1
    body = json.loads(create.calls[0].request.content)
    if expected is _MISSING:
        assert "access" not in body
    else:
        assert body["access"] == expected
    assert "organizationId" not in body
    assert "organization_id" not in body


@pytest.mark.parametrize("tool", (*_CREATORS, "divoid_patch_node"))
async def test_invalid_access_never_mutates_http(server: FastMCP, tool: str) -> None:
    args = {"access": "OrgRead|Unknown"}
    if tool == "divoid_patch_node":
        args["id"] = 99
    else:
        args.update(name="Invalid fixture", content="body")
        if tool != "divoid_create_node":
            args["project_id"] = 3

    with respx.mock(assert_all_called=False) as mock:
        mock.get(f"{_BASE}/nodes").respond(200, json={"result": [{"id": 7}]})
        result = await server._tool_manager.call_tool(tool, args)

    assert result["isError"] is True
    assert result["content"][0]["text"].startswith("access_invalid_value:")
    assert not any(call.request.method != "GET" for call in mock.calls)


@pytest.mark.parametrize("access, expected", [("Write|OrgRead", 6), (31, 31)])
async def test_patch_forwards_access_and_server_metadata(
    server: FastMCP, access: int | str, expected: int,
) -> None:
    response = {"id": 99, "access": expected, "organizationId": 42}
    with respx.mock(assert_all_called=True) as mock:
        patch = mock.patch(f"{_BASE}/nodes/99").respond(200, json=response)
        result = await server._tool_manager.call_tool("divoid_patch_node", {"id": 99, "access": access})

    assert json.loads(patch.calls[0].request.content) == [
        {"op": "replace", "path": "/access", "value": expected},
    ]
    assert result == response


@pytest.mark.parametrize("organization", [42, None, _MISSING])
async def test_get_node_preserves_nullable_organization_and_existing_metadata(
    server: FastMCP, organization: object,
) -> None:
    response = {"id": 99, "access": 31, "ownerId": 5, "rootNodeId": 7, "name": "Fixture"}
    if organization is not _MISSING:
        response["organizationId"] = organization
    with respx.mock(assert_all_called=True) as mock:
        mock.get(f"{_BASE}/nodes/99").respond(200, json=response)
        result = await server._tool_manager.call_tool("divoid_get_node", {"id": 99})

    assert result["organizationId"] == (None if organization is _MISSING else organization)
    assert all(result[key] == value for key, value in response.items())
