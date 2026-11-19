"""One-connection NDJSON bridge for a supervising inbox consumer."""

from __future__ import annotations

import asyncio
import json
import logging
import signal
import sys
from contextlib import aclosing

from . import http_client
from .config import load_secret
from .inbox_event_error import InboxEventError


async def _run() -> int:
    config_logger = logging.getLogger("divoid_mcp.config")
    disabled = config_logger.disabled
    config_logger.disabled = True
    try:
        config = load_secret()
    except (SystemExit, Exception):
        print("inbox_events error=configuration", file=sys.stderr, flush=True)
        return 2
    finally:
        config_logger.disabled = disabled
    try:
        http_client.init(config.base_url, config.api_key)
        async with aclosing(http_client.inbox_events()) as events:
            async for event in events:
                print(json.dumps(event, separators=(",", ":")), flush=True)
    except InboxEventError as error:
        print(f"inbox_events error={error.code}", file=sys.stderr, flush=True)
        return 2 if error.permanent else 1
    except (BrokenPipeError, ConnectionResetError, asyncio.CancelledError):
        return 130
    except Exception:
        print("inbox_events error=internal", file=sys.stderr, flush=True)
        return 1
    finally:
        try:
            await http_client.close()
        except Exception:
            print("inbox_events error=close", file=sys.stderr, flush=True)
    return 1


async def _supervised() -> int:
    loop = asyncio.get_running_loop()
    task = asyncio.current_task()
    installed = False
    try:
        loop.add_signal_handler(signal.SIGTERM, task.cancel)
        installed = True
    except (NotImplementedError, RuntimeError, ValueError):
        pass
    try:
        return await _run()
    finally:
        if installed:
            loop.remove_signal_handler(signal.SIGTERM)


def main() -> int:
    """Run once; the parent owns reconnect and authoritative bootstrap."""
    try:
        return asyncio.run(_supervised())
    except KeyboardInterrupt:
        return 130
