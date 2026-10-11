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


def test_map_http_error_404_message_names_access_denial():
    """A 404 may mean 'absent' or 'no access'; the message must not claim only absence."""
    _, message = errors.map_http_error(404, b"", api_key="k")

    assert "does not exist" in message
    assert "access" in message
    assert message.index("does not exist") < message.index("access")


def test_map_http_error_404_code_unchanged():
    """Callers match on the code; rewording the message must not rename it."""
    code, _ = errors.map_http_error(404, b"", api_key="k")

    assert code == "node_not_found"


def test_map_http_error_404_uses_explanation():
    code, message = errors.map_http_error(
        404, b"", api_key="k", context="divoid_x", not_found_explanation="The edge is missing."
    )

    assert code == "node_not_found"
    assert message.startswith("divoid_x: DiVoid returned 404. ")
    assert "The edge is missing." in message
    assert "The node does not exist" not in message


def test_map_http_error_explanation_ignored_on_non_404():
    body = b'{"code":"bad","text":"nope"}'

    code, message = errors.map_http_error(
        400, body, api_key="k", not_found_explanation="The edge is missing."
    )

    assert code == "divoid_bad_request"
    assert "nope" in message
    assert "The edge is missing." not in message


def test_backend_error_text_extracts_and_tolerates():
    envelope = b'{"code":"data_entitynotfound","text":"\'Node\' with id \'5\' not found"}'

    assert errors.backend_error_text(envelope) == "'Node' with id '5' not found"
    assert errors.backend_error_text(b"not json at all") == ""
    assert errors.backend_error_text(b"[1, 2]") == ""
    assert errors.backend_error_text(b'{"code":"x"}') == ""
    assert errors.backend_error_text(b"") == ""
