"""Unit tests for divoid_mcp.errors -- HTTP-status-to-MCP-error message mapping."""

from __future__ import annotations

from divoid_mcp import errors


def test_401_hint_names_the_env_variable_and_no_hardcoded_path():
    code, message = errors.map_http_error(401, b"", api_key="k")

    assert code == "divoid_unauthorized"
    assert "DIVOID_MCP_API_KEY" in message
    assert "fallback credentials file" in message
    assert 'The startup log line "Config loaded: source=..." names which one was used' in message
    assert ".divoid-online" not in message


def test_401_prefixes_context_when_given():
    code, message = errors.map_http_error(401, b"", api_key="k", context="divoid_search")

    assert message.startswith("divoid_search: ")


def test_401_omits_context_prefix_when_not_given():
    _, message = errors.map_http_error(401, b"", api_key="k")

    assert not message.startswith(": ")


def test_401_never_echoes_the_api_key():
    sentinel = "sentinel-do-not-leak-9f31"

    _, message = errors.map_http_error(401, b"", api_key=sentinel)

    assert sentinel not in message
