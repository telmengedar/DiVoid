"""Bounded fixed-protocol decoder; message payloads and routing fields are rejected."""

from __future__ import annotations

import codecs
import json
from collections.abc import AsyncIterator
from typing import Any

from .inbox_event_error import InboxEventError

_READY = {"version": 1, "coverage": "postgres-database", "replay": False,
          "resyncRequired": True}


def _object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate")
        result[key] = value
    return result


async def decode(chunks: AsyncIterator[bytes]) -> AsyncIterator[dict[str, Any]]:
    """Accept only bounded ready/invalidate frames; never fabricate readiness."""
    decoder = codecs.getincrementaldecoder("utf-8")("strict")
    buffer = ""
    event = None
    data = None
    size = 0
    ready = False
    try:
        async for chunk in chunks:
            if len(chunk) > 4096:
                raise InboxEventError("protocol")
            buffer += decoder.decode(chunk)
            while "\n" in buffer:
                line, buffer = buffer.split("\n", 1)
                line = line.removesuffix("\r")
                if len(line) > 4096:
                    raise InboxEventError("protocol")
                if line.startswith(":"):
                    continue
                if not line:
                    if event is None and data is None:
                        continue
                    if event is None or data is None:
                        raise InboxEventError("protocol")
                    value = json.loads(data, object_pairs_hook=_object)
                    if event == "ready" and not ready and value == _READY:
                        if (type(value.get("version")) is not int
                                or value.get("replay") is not False
                                or value.get("resyncRequired") is not True):
                            raise InboxEventError("protocol")
                        ready = True
                        yield {"event": "ready", **_READY}
                    elif event == "invalidate" and ready and value == {}:
                        yield {"event": "invalidate"}
                    else:
                        raise InboxEventError("protocol")
                    event = data = None
                    size = 0
                    continue
                size += len(line) + 1
                if size > 4096 or ":" not in line:
                    raise InboxEventError("protocol")
                field, value = line.split(":", 1)
                value = value.removeprefix(" ")
                if field == "event" and event is None:
                    event = value
                elif field == "data" and data is None:
                    data = value
                else:
                    raise InboxEventError("protocol")
            if len(buffer) > 4096:
                raise InboxEventError("protocol")
        decoder.decode(b"", final=True)
    except (UnicodeError, ValueError, TypeError):
        raise InboxEventError("protocol") from None
    raise InboxEventError("eof")
