"""Unit tests for divoid_mcp.server -- the MCP `instructions` string built from resolved config."""

from __future__ import annotations

import logging
import os

import pytest

from divoid_mcp import http_client, server
from divoid_mcp.config import DivoidConfig

_BASE_INSTRUCTIONS_SENTENCE = "wraps the DiVoid graph API. Start with divoid_search"
_DEPRECATION_SENTENCE = (
    "NOTE: this server started from the DEPRECATED credentials file fallback; ask your "
    "operator to move the DiVoid credentials into the MCP client's env block"
)


def test_instructions_carry_deprecation_sentence_only_on_file_source():
    file_config = DivoidConfig(
        base_url="https://example/api", api_key="k", source="file:/some/path"
    )
    env_config = DivoidConfig(base_url="https://example/api", api_key="k", source="env")

    file_instructions = server._build_instructions(file_config)
    env_instructions = server._build_instructions(env_config)

    assert _DEPRECATION_SENTENCE in file_instructions
    assert _DEPRECATION_SENTENCE not in env_instructions


def test_instructions_contain_the_base_description_on_both_sources():
    file_config = DivoidConfig(
        base_url="https://example/api", api_key="k", source="file:/some/path"
    )
    env_config = DivoidConfig(base_url="https://example/api", api_key="k", source="env")

    assert _BASE_INSTRUCTIONS_SENTENCE in server._build_instructions(file_config)
    assert _BASE_INSTRUCTIONS_SENTENCE in server._build_instructions(env_config)


_API_KEY = "sentinel-api-key-value"


@pytest.fixture
def startup_env(monkeypatch):
    """A valid credential environment with the other startup inputs cleared, and the
    http_client module state restored afterwards."""
    monkeypatch.setenv("DIVOID_MCP_URL", "https://example/api")
    monkeypatch.setenv("DIVOID_MCP_API_KEY", _API_KEY)
    for name in ("SSL_CERT_FILE", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"):
        monkeypatch.delenv(name, raising=False)
    monkeypatch.setattr(http_client, "_client", None)
    monkeypatch.setattr(http_client, "_base_url", "")


def _run_main_expecting_exit(caplog) -> str:
    with caplog.at_level(logging.ERROR):
        with pytest.raises(SystemExit) as excinfo:
            server.main()
    assert excinfo.value.code == 1
    return "\n".join(rec.message for rec in caplog.records)


def test_unreadable_ca_bundle_exits_naming_the_variable_and_its_value(
    startup_env, monkeypatch, tmp_path, caplog
):
    stale = str(tmp_path / "gone" / "corp-ca.pem")
    monkeypatch.setenv("SSL_CERT_FILE", stale)

    text = _run_main_expecting_exit(caplog)

    assert "Initialising the HTTP client failed" in text
    assert "SSL_CERT_FILE" in text
    assert stale in text


def test_http_client_failure_omits_the_credential_hint_and_the_key(
    startup_env, monkeypatch, tmp_path, caplog
):
    monkeypatch.setenv("SSL_CERT_FILE", str(tmp_path / "gone.pem"))

    text = _run_main_expecting_exit(caplog)

    assert "claude mcp add" not in text
    assert _API_KEY not in text


def test_unsupported_proxy_scheme_exits_without_echoing_the_proxy_password(
    startup_env, monkeypatch, caplog
):
    monkeypatch.setenv("HTTPS_PROXY", "ftp://user:proxy-secret@proxy.example:1")

    text = _run_main_expecting_exit(caplog)

    assert "Initialising the HTTP client failed" in text
    assert "HTTPS_PROXY" in text
    assert "proxy-secret" not in text


def test_unresolvable_working_directory_exits_naming_the_root_variable(
    startup_env, monkeypatch, caplog
):
    monkeypatch.delenv("DIVOID_MCP_FILE_ROOT", raising=False)
    monkeypatch.setattr(http_client, "init", lambda base_url, api_key: None)

    def deleted_cwd() -> str:
        raise FileNotFoundError(2, "No such file or directory")

    monkeypatch.setattr(os, "getcwd", deleted_cwd)

    text = _run_main_expecting_exit(caplog)

    assert "Initialising the filesystem path roots failed" in text
    assert "DIVOID_MCP_FILE_ROOT" in text
    assert "claude mcp add" not in text
