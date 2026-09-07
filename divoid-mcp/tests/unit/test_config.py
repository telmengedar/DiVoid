"""Unit tests for divoid_mcp.config -- credential resolution order and fail-closed diagnostics."""

from __future__ import annotations

import logging

import pytest

from divoid_mcp import config


def _write_file(path, url: str = "https://file.example/api", api_key: str = "file-key") -> None:
    path.write_text(f"Url={url}\nApiKey={api_key}\n", encoding="utf-8")


def test_env_source_wins_over_fallback_file(tmp_path):
    path = tmp_path / ".divoid-online"
    _write_file(path, url="https://file.example/api")
    env = {config.ENV_URL: "https://env.example/api", config.ENV_API_KEY: "env-key"}

    result = config.load_secret(env=env, path=path)

    assert result.base_url == "https://env.example/api"
    assert result.source == "env"


def test_env_none_defaults_to_os_environ(monkeypatch, tmp_path):
    monkeypatch.setenv(config.ENV_API_KEY, "os-environ-key")
    monkeypatch.setenv(config.ENV_URL, "https://os-environ.example/api")

    result = config.load_secret(path=tmp_path / "unused")

    assert result.api_key == "os-environ-key"
    assert result.base_url == "https://os-environ.example/api"
    assert result.source == "env"


def test_env_api_key_without_url_exits_without_reading_the_file(tmp_path, caplog):
    path = tmp_path / ".divoid-online"
    _write_file(path)
    env = {config.ENV_API_KEY: "env-key"}

    with caplog.at_level(logging.ERROR):
        with pytest.raises(SystemExit) as exc_info:
            config.load_secret(env=env, path=path)

    assert exc_info.value.code == 1
    text = "\n".join(rec.message for rec in caplog.records)
    assert "DIVOID_MCP_API_KEY is set but DIVOID_MCP_URL is empty or missing" in text


def test_env_url_without_api_key_exits_without_reading_the_file(tmp_path):
    path = tmp_path / ".divoid-online"
    _write_file(path, url="https://production.example/api", api_key="production-key")
    env = {config.ENV_URL: "https://second-instance.example/api"}

    with pytest.raises(SystemExit) as exc_info:
        config.load_secret(env=env, path=path)
    assert exc_info.value.code == 1


def test_env_url_without_api_key_message_names_the_key_and_the_file(tmp_path, caplog):
    path = tmp_path / ".divoid-online"
    _write_file(path, url="https://production.example/api", api_key="production-key")
    env = {config.ENV_URL: "https://second-instance.example/api"}

    with caplog.at_level(logging.ERROR):
        with pytest.raises(SystemExit):
            config.load_secret(env=env, path=path)

    text = "\n".join(rec.message for rec in caplog.records)
    assert "DIVOID_MCP_URL is set but DIVOID_MCP_API_KEY is empty or missing" in text
    assert "DIVOID_MCP_API_KEY" in text
    assert str(path) in text
    assert "NOT consulted" in text


def test_empty_env_url_falls_back_to_file(tmp_path):
    path = tmp_path / ".divoid-online"
    _write_file(path, url="https://file.example/api", api_key="file-key")

    empty_result = config.load_secret(env={config.ENV_URL: ""}, path=path)
    assert empty_result.source == f"file:{path}"
    assert empty_result.base_url == "https://file.example/api"

    whitespace_result = config.load_secret(env={config.ENV_URL: "   \n"}, path=path)
    assert whitespace_result.source == f"file:{path}"
    assert whitespace_result.base_url == "https://file.example/api"


def test_falls_back_to_file_when_api_key_absent(tmp_path):
    path = tmp_path / ".divoid-online"
    _write_file(path, url="https://file.example/api", api_key="file-key")

    result = config.load_secret(env={}, path=path)

    assert result.base_url == "https://file.example/api"
    assert result.api_key == "file-key"
    assert result.source == f"file:{path}"


def test_empty_env_api_key_falls_back_to_file(tmp_path):
    path = tmp_path / ".divoid-online"
    _write_file(path, url="https://file.example/api", api_key="file-key")
    env = {config.ENV_API_KEY: ""}

    result = config.load_secret(env=env, path=path)

    assert result.source == f"file:{path}"
    assert result.base_url == "https://file.example/api"


def test_env_values_are_stripped(tmp_path):
    env = {
        config.ENV_API_KEY: "env-key\n",
        config.ENV_URL: "https://env.example/api\n",
    }

    result = config.load_secret(env=env, path=tmp_path / "unused")

    assert result.api_key == "env-key"
    assert result.base_url == "https://env.example/api"


def test_no_credentials_message_names_both_variables_and_the_path(tmp_path, caplog):
    path = tmp_path / ".divoid-online"

    with caplog.at_level(logging.ERROR):
        with pytest.raises(SystemExit) as exc_info:
            config.load_secret(env={}, path=path)

    assert exc_info.value.code == 1
    text = "\n".join(rec.message for rec in caplog.records)
    assert "No DiVoid credentials found -- divoid-mcp cannot start." in text
    assert (
        "Neither DIVOID_MCP_URL nor DIVOID_MCP_API_KEY is set, and the deprecated "
        "fallback credentials file was not found at" in text
    )
    assert str(path) in text
    assert "claude mcp add --transport stdio" in text


def test_malformed_file_missing_url_line_names_url(tmp_path, caplog):
    path = tmp_path / ".divoid-online"
    path.write_text("ApiKey=file-key\n", encoding="utf-8")

    with caplog.at_level(logging.ERROR):
        with pytest.raises(SystemExit):
            config.load_secret(env={}, path=path)

    text = "\n".join(rec.message for rec in caplog.records)
    assert "is malformed: no 'Url=' line" in text
    assert "no 'ApiKey=' line" not in text


def test_malformed_file_missing_apikey_line_names_apikey(tmp_path, caplog):
    path = tmp_path / ".divoid-online"
    path.write_text("Url=https://file.example/api\n", encoding="utf-8")

    with caplog.at_level(logging.ERROR):
        with pytest.raises(SystemExit):
            config.load_secret(env={}, path=path)

    text = "\n".join(rec.message for rec in caplog.records)
    assert "is malformed: no 'ApiKey=' line" in text
    assert "no 'Url=' line" not in text


def test_empty_value_file_url_empty_names_url(tmp_path, caplog):
    path = tmp_path / ".divoid-online"
    path.write_text("Url=\nApiKey=file-key\n", encoding="utf-8")

    with caplog.at_level(logging.ERROR):
        with pytest.raises(SystemExit):
            config.load_secret(env={}, path=path)

    text = "\n".join(rec.message for rec in caplog.records)
    assert "has an empty 'Url=' value" in text
    assert "empty 'ApiKey=' value" not in text


def test_empty_value_file_apikey_empty_names_apikey(tmp_path, caplog):
    path = tmp_path / ".divoid-online"
    path.write_text("Url=https://file.example/api\nApiKey=\n", encoding="utf-8")

    with caplog.at_level(logging.ERROR):
        with pytest.raises(SystemExit):
            config.load_secret(env={}, path=path)

    text = "\n".join(rec.message for rec in caplog.records)
    assert "has an empty 'ApiKey=' value" in text
    assert "empty 'Url=' value" not in text


@pytest.mark.parametrize(
    "make_env_and_path",
    [
        pytest.param(lambda tmp_path: ({}, tmp_path / "missing"), id="file_absent"),
        pytest.param(
            lambda tmp_path: ({}, _make_unreadable(tmp_path)), id="file_unreadable"
        ),
        pytest.param(lambda tmp_path: ({}, _make_empty(tmp_path)), id="file_empty"),
        pytest.param(
            lambda tmp_path: ({}, _make_missing_line(tmp_path)), id="file_missing_line"
        ),
        pytest.param(
            lambda tmp_path: ({}, _make_empty_value(tmp_path)), id="file_empty_value"
        ),
        pytest.param(
            lambda tmp_path: ({config.ENV_API_KEY: "k"}, _make_valid(tmp_path)),
            id="env_without_url",
        ),
        pytest.param(
            lambda tmp_path: (
                {config.ENV_URL: "https://second.example/api"}, _make_valid(tmp_path)
            ),
            id="env_without_api_key",
        ),
    ],
)
def test_every_failure_branch_exits_nonzero(tmp_path, make_env_and_path):
    env, path = make_env_and_path(tmp_path)

    with pytest.raises(SystemExit) as exc_info:
        config.load_secret(env=env, path=path)
    assert exc_info.value.code == 1


def _make_unreadable(tmp_path):
    """A directory stands in for the file: read_text() raises OSError everywhere, unlike
    chmod bits which Windows does not enforce (docs.python.org/3/library/os.html#os.chmod)."""
    path = tmp_path / "unreadable" / ".divoid-online"
    path.mkdir(parents=True)
    return path


def _make_empty(tmp_path):
    path = tmp_path / "empty" / ".divoid-online"
    path.parent.mkdir()
    path.write_text("", encoding="utf-8")
    return path


def _make_missing_line(tmp_path):
    path = tmp_path / "missing_line" / ".divoid-online"
    path.parent.mkdir()
    path.write_text("Url=https://file.example/api\n", encoding="utf-8")
    return path


def _make_empty_value(tmp_path):
    path = tmp_path / "empty_value" / ".divoid-online"
    path.parent.mkdir()
    path.write_text("Url=\nApiKey=file-key\n", encoding="utf-8")
    return path


def _make_valid(tmp_path):
    path = tmp_path / "valid" / ".divoid-online"
    path.parent.mkdir()
    _write_file(path)
    return path


def test_api_key_never_appears_in_any_log_record(tmp_path, caplog):
    sentinel = "sentinel-do-not-leak-9f31"
    env = {config.ENV_API_KEY: sentinel, config.ENV_URL: "https://env.example/api"}

    with caplog.at_level(logging.DEBUG):
        config.load_secret(env=env, path=tmp_path / "unused")

    for record in caplog.records:
        assert sentinel not in record.message


def test_source_field_never_contains_the_api_key(tmp_path):
    sentinel = "sentinel-do-not-leak-9f31"

    env_result = config.load_secret(
        env={config.ENV_API_KEY: sentinel, config.ENV_URL: "https://env.example/api"},
        path=tmp_path / "unused",
    )
    assert sentinel not in env_result.source

    file_path = tmp_path / ".divoid-online"
    _write_file(file_path, api_key=sentinel)
    file_result = config.load_secret(env={}, path=file_path)
    assert sentinel not in file_result.source


def test_deprecation_warning_only_on_file_source(tmp_path, caplog):
    file_path = tmp_path / ".divoid-online"
    _write_file(file_path)

    with caplog.at_level(logging.WARNING):
        config.load_secret(env={}, path=file_path)
    assert any("DEPRECATED credential source" in rec.message for rec in caplog.records)
    assert any("claude mcp add --transport stdio" in rec.message for rec in caplog.records)

    caplog.clear()
    with caplog.at_level(logging.WARNING):
        config.load_secret(
            env={config.ENV_API_KEY: "k", config.ENV_URL: "https://env.example/api"},
            path=file_path,
        )
    assert not any("DEPRECATED credential source" in rec.message for rec in caplog.records)
