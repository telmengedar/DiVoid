"""
Credential loader for divoid-mcp.

Resolves DiVoid credentials from the process environment (DIVOID_MCP_URL /
DIVOID_MCP_API_KEY) as the primary source, falling back to the legacy
secrets file (~/.claude/secrets/.divoid-online) only when the environment
provides neither variable. Once either variable is set, both are required
and the file is not consulted. Fail-closed: any missing/malformed/incomplete
state exits the process before the stdio loop starts.
"""

from __future__ import annotations

import logging
import os
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Final, NoReturn

logger = logging.getLogger(__name__)

ENV_URL: Final[str] = "DIVOID_MCP_URL"
ENV_API_KEY: Final[str] = "DIVOID_MCP_API_KEY"

LEGACY_SECRET_FILE_PATH = Path.home() / ".claude" / "secrets" / ".divoid-online"

_ENV_HINT: Final[str] = (
    "divoid-mcp takes its credentials from the environment (MCP specification 2025-06-18, "
    "Authorization: stdio servers retrieve credentials from the environment). Set both of "
    "these in the \"env\" block of this server's entry in your MCP client configuration:\n"
    f"    {ENV_URL}      the DiVoid API base URL, including the /api suffix\n"
    "                        (for the mamgo instance: https://divoid.mamgo.io/api)\n"
    f"    {ENV_API_KEY}  your DiVoid API key\n"
    "\"command\" is the absolute path to the divoid-mcp console script in its dedicated\n"
    "venv, not \"python\" -- that way it does not depend on which interpreter is first on\n"
    "this host's PATH:\n"
    "    POSIX:   ~/.divoid-mcp/venv/bin/divoid-mcp\n"
    "    Windows: %USERPROFILE%\\.divoid-mcp\\venv\\Scripts\\divoid-mcp.exe\n"
    "In Claude Code:\n"
    "    claude mcp add --transport stdio --scope user divoid \\\n"
    f"      -e {ENV_URL}=<url> -e {ENV_API_KEY}=<key> \\\n"
    "      -- ~/.divoid-mcp/venv/bin/divoid-mcp\n"
    "Full instructions: divoid-mcp/docs/install.md (DiVoid node #829)."
)

_MALFORMED_LINE_MSG: Final[str] = (
    "The fallback credentials file {path} is malformed: no '{prefix}' line -- "
    "divoid-mcp cannot start."
)
_EMPTY_VALUE_MSG: Final[str] = (
    "The fallback credentials file {path} has an empty '{key}=' value -- "
    "divoid-mcp cannot start."
)


@dataclass(frozen=True)
class DivoidConfig:
    """Frozen container for the resolved DiVoid base URL, api key, and credential source."""

    base_url: str
    api_key: str
    source: str


def load_secret(
    env: "os._Environ[str] | dict[str, str] | None" = None,
    path: Path = LEGACY_SECRET_FILE_PATH,
) -> DivoidConfig:
    """
    Resolve DiVoid credentials from the environment, falling back to the legacy secrets
    file at `path` when neither DIVOID_MCP_URL nor DIVOID_MCP_API_KEY is set. Exits the
    process (non-zero) on any missing, malformed, or incomplete credential source.
    """
    if env is None:
        env = os.environ

    env_selected = bool(env.get(ENV_URL, "").strip()) or bool(env.get(ENV_API_KEY, "").strip())
    if env_selected:
        return _from_env(env, path)

    return _from_fallback_file(path)


def _from_env(env: "os._Environ[str] | dict[str, str]", path: Path) -> DivoidConfig:
    """Builds a DivoidConfig from the environment; fails closed if either DIVOID_MCP_URL
    or DIVOID_MCP_API_KEY is empty or missing. `path` names the fallback file in the E2
    message; it is not read here."""
    base_url = env.get(ENV_URL, "").strip()
    api_key = env.get(ENV_API_KEY, "").strip()

    if not base_url:
        _fail(
            f"{ENV_API_KEY} is set but {ENV_URL} is empty or missing -- divoid-mcp cannot "
            "start.\n"
            f"When either {ENV_URL} or {ENV_API_KEY} is set, the environment is the "
            "credential source and both variables are required; the deprecated fallback "
            "credentials file is not consulted."
        )
    if not api_key:
        _fail(
            f"{ENV_URL} is set but {ENV_API_KEY} is empty or missing -- divoid-mcp cannot "
            "start.\n"
            f"When either {ENV_URL} or {ENV_API_KEY} is set, the environment is the "
            "credential source and both variables are required. The deprecated fallback "
            f"credentials file at {path} was NOT consulted, even if it exists -- using "
            f"its key with the {ENV_URL} set here would pair credentials from two "
            "different sources, which is how a request reaches the wrong host.\n"
            "The most common cause is a misspelled variable name: check that the key is "
            f"spelled exactly {ENV_API_KEY} in the same \"env\" block."
        )

    logger.info("Config loaded: source=env base_url=%s key=***", base_url)
    return DivoidConfig(base_url=base_url, api_key=api_key, source="env")


def _from_fallback_file(path: Path) -> DivoidConfig:
    """Parses the legacy two-line secrets file at path and builds a DivoidConfig; fails
    closed on any parse error."""
    try:
        text = path.read_text(encoding="utf-8")
    except FileNotFoundError:
        _fail(
            "No DiVoid credentials found -- divoid-mcp cannot start.\n"
            f"Neither {ENV_URL} nor {ENV_API_KEY} is set, and the deprecated fallback "
            f"credentials file was not found at {path}."
        )
    except OSError as exc:
        _fail(
            f"Cannot read the fallback credentials file {path}: {exc} -- divoid-mcp "
            "cannot start."
        )

    if not text.strip():
        _fail(f"The fallback credentials file {path} is empty -- divoid-mcp cannot start.")

    base_url: str | None = None
    api_key: str | None = None

    for line in text.splitlines():
        line = line.strip()
        if line.startswith("Url="):
            base_url = line[len("Url="):]
        elif line.startswith("ApiKey="):
            api_key = line[len("ApiKey="):]

    if base_url is None:
        _fail(_MALFORMED_LINE_MSG.format(path=path, prefix="Url="))
    if api_key is None:
        _fail(_MALFORMED_LINE_MSG.format(path=path, prefix="ApiKey="))
    if not base_url:
        _fail(_EMPTY_VALUE_MSG.format(path=path, key="Url"))
    if not api_key:
        _fail(_EMPTY_VALUE_MSG.format(path=path, key="ApiKey"))

    logger.warning(
        "DEPRECATED credential source: divoid-mcp started from the fallback credentials "
        "file %s.\nThis fallback will be removed. Move the credentials into the \"env\" "
        "block of this server's entry in your MCP client configuration.\n%s",
        path, _ENV_HINT,
    )
    logger.info("Config loaded: source=file:%s base_url=%s key=***", path, base_url)
    return DivoidConfig(base_url=base_url, api_key=api_key, source=f"file:{path}")


def _fail(message: str) -> NoReturn:
    """Logs message plus the environment-source hint to stderr, then exits the process
    with status 1."""
    logger.error("%s\n%s", message, _ENV_HINT)
    sys.exit(1)
