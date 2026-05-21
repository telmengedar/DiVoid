"""
Secret loader for divoid-mcp.

Reads ~/.claude/secrets/.divoid-online (two-line Url=/ApiKey= format) at
startup and produces a frozen DivoidConfig container. Fail-closed: any
missing/malformed state exits the process before the stdio loop starts.

The api_key is held only here and inside the HTTP client's pre-built
Authorization header. No other module holds the raw value.
"""

from __future__ import annotations

import logging
import os
import sys
from dataclasses import dataclass
from pathlib import Path

logger = logging.getLogger(__name__)

SECRET_FILE_PATH = Path.home() / ".claude" / "secrets" / ".divoid-online"


@dataclass(frozen=True)
class DivoidConfig:
    base_url: str
    api_key: str


def load_secret(path: Path = SECRET_FILE_PATH) -> DivoidConfig:
    """
    Parse the two-line secret file and return a frozen config container.

    Exits the process (non-zero) if the file is absent, empty, or missing
    either required line. This is intentional: a server without auth can
    only produce 401s, which would be confusingly misattributed.
    """
    try:
        text = path.read_text(encoding="utf-8")
    except FileNotFoundError:
        logger.error("Secret file not found: %s — cannot start without DiVoid credentials.", path)
        sys.exit(1)
    except PermissionError as exc:
        logger.error("Cannot read secret file %s: %s", path, exc)
        sys.exit(1)
    except OSError as exc:
        logger.error("Cannot read secret file %s: %s", path, exc)
        sys.exit(1)

    if not text.strip():
        logger.error("Secret file %s is empty — cannot start.", path)
        sys.exit(1)

    base_url: str | None = None
    api_key: str | None = None

    for line in text.splitlines():
        line = line.strip()
        if line.startswith("Url="):
            base_url = line[len("Url="):]
        elif line.startswith("ApiKey="):
            api_key = line[len("ApiKey="):]

    if base_url is None:
        logger.error("Secret file %s is malformed: missing 'Url=' line.", path)
        sys.exit(1)

    if api_key is None:
        logger.error("Secret file %s is malformed: missing 'ApiKey=' line.", path)
        sys.exit(1)

    if not base_url:
        logger.error("Secret file %s has an empty 'Url=' value.", path)
        sys.exit(1)

    if not api_key:
        logger.error("Secret file %s has an empty 'ApiKey=' value.", path)
        sys.exit(1)

    # Log the URL (safe) but never the key value.
    logger.info("Config loaded: base_url=%s key=***", base_url)
    return DivoidConfig(base_url=base_url, api_key=api_key)
