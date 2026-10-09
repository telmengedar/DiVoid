"""
Bootstrap module for divoid-mcp.

Startup sequence (see architecture §14 / §6.1):
1. Configure logging to stderr at the level from DIVOID_MCP_LOG_LEVEL.
2. Resolve DiVoid credentials from the environment or the fallback file (fail-closed on
   missing/malformed).
3. Initialise the shared HTTP client with auth header pre-set.
3b. Initialise the filesystem path containment roots (paths.py).
4. Run the drift canary (warn on mismatch, never block startup).
5. Create the MCP server, register tools and resources.
6. Enter the stdio event loop (blocks until the host closes the stream).
7. On clean exit, close the HTTP client.

The api_key never appears outside of config and http_client.
All logs go to stderr — stdout is the JSON-RPC stream.
"""

from __future__ import annotations

import asyncio
import logging
import os
import sys
from collections.abc import Callable

from . import http_client, paths
from .config import DivoidConfig, _fail, load_secret
from .drift import run_canary
from .resources import register_resources
from .tools import register_tools
from .version import __version__

logger = logging.getLogger(__name__)

_CA_BUNDLE_ENV_VAR = "SSL_CERT_FILE"


def _configure_logging() -> None:
    level_name = os.environ.get("DIVOID_MCP_LOG_LEVEL", "INFO").upper()
    level = getattr(logging, level_name, logging.INFO)
    logging.basicConfig(
        level=level,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
        stream=sys.stderr,
    )


def main() -> None:
    """Entry point for the console script and python -m divoid_mcp."""
    _configure_logging()
    logger.info("divoid-mcp %s starting.", __version__)

    # Step 2: load config (exits non-zero on failure — see config.py §6.4)
    config = load_secret()

    # Step 3: initialise shared HTTP client
    _run_startup_step(
        "Initialising the HTTP client",
        lambda: http_client.init(config.base_url, config.api_key),
        _http_failure_hint(),
    )

    _run_startup_step(
        "Initialising the filesystem path roots",
        paths.init,
        f"{paths.ENV_VAR} is read here; when it is unset or empty the process working "
        "directory is used, and that directory must still exist and be readable.",
    )

    # Run the async startup and serve
    asyncio.run(_async_main(config))


def _http_failure_hint() -> str:
    """Names the environment inputs httpx reads while building its client, with the CA
    bundle path (proxy variables are named, never echoed)."""
    value = os.environ.get(_CA_BUNDLE_ENV_VAR)
    current = "unset" if value is None else f'"{value}"'
    return (
        f"httpx builds its TLS context at this point and honours {_CA_BUNDLE_ENV_VAR}; a "
        f"stale or unreadable path there makes this step fail (currently: {current}). "
        "Correct or unset it in the \"env\" block of this server's entry in your MCP client "
        "configuration. An HTTP_PROXY / HTTPS_PROXY / ALL_PROXY (or lowercase http_proxy / "
        "https_proxy / all_proxy) with an unsupported scheme fails here too."
    )


def _run_startup_step(what: str, step: Callable[[], None], cause_hint: str) -> None:
    """Runs one post-credential startup step; any exception leaves through config._fail
    with the step's name, the exception, and the environment inputs that can cause it."""
    try:
        step()
    except Exception as exc:
        _fail(
            f"{what} failed: {type(exc).__name__}: {exc} -- divoid-mcp cannot start.\n"
            f"{cause_hint}",
            with_env_hint=False,
        )


def _build_instructions(config: DivoidConfig) -> str:
    """Builds the MCP `instructions` string, appending a deprecation notice when
    config.source is the fallback file."""
    instructions = (
        f"divoid-mcp {__version__} — wraps the DiVoid graph API. "
        "Start with divoid_search for question-shaped queries. "
        "Use divoid_get_node to inspect metadata, divoid_get_content for bodies. "
        "Resources divoid://node/9 and divoid://node/190 carry the operating conventions."
    )
    if config.source.startswith("file:"):
        instructions += (
            " NOTE: this server started from the DEPRECATED credentials file fallback; "
            "ask your operator to move the DiVoid credentials into the MCP client's env "
            "block (DIVOID_MCP_URL / DIVOID_MCP_API_KEY) before that fallback is removed."
        )
    return instructions


async def _async_main(config: DivoidConfig) -> None:
    # Step 4: drift canary
    await run_canary()

    # Step 5: create MCP server, register tools and resources
    from mcp.server.fastmcp import FastMCP

    mcp_server = FastMCP(
        "divoid-mcp",
        instructions=_build_instructions(config),
    )

    # Attach config so tool dispatchers can access the api_key for redaction.
    mcp_server.config = config  # type: ignore[attr-defined]

    register_tools(mcp_server)
    register_resources(mcp_server)

    logger.info("divoid-mcp ready; entering stdio loop.")

    # Step 6: enter the stdio event loop
    try:
        await mcp_server.run_stdio_async()
    finally:
        # Step 7: clean shutdown
        await http_client.close()
        logger.info("divoid-mcp shut down cleanly.")
