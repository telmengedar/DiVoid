"""
Bootstrap module for divoid-mcp.

Startup sequence (see architecture §14 / §6.1):
1. Configure logging to stderr at the level from DIVOID_MCP_LOG_LEVEL.
2. Load the DiVoid secret (fail-closed on missing/malformed).
3. Initialise the shared HTTP client with auth header pre-set.
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

from . import http_client
from .config import load_secret
from .drift import run_canary
from .resources import register_resources
from .tools import register_tools
from .version import __version__

logger = logging.getLogger(__name__)


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
    http_client.init(config.base_url, config.api_key)

    # Run the async startup and serve
    asyncio.run(_async_main(config))


async def _async_main(config) -> None:
    # Step 4: drift canary
    await run_canary()

    # Step 5: create MCP server, register tools and resources
    from mcp.server.fastmcp import FastMCP

    mcp_server = FastMCP(
        "divoid-mcp",
        version=__version__,
        instructions=(
            "This server wraps the DiVoid graph API. "
            "Start with divoid_search for question-shaped queries. "
            "Use divoid_get_node to inspect metadata, divoid_get_content for bodies. "
            "Resources divoid://node/9 and divoid://node/190 carry the operating conventions."
        ),
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
