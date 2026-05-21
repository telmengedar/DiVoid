"""
Shared async HTTP client for divoid-mcp.

One module-level AsyncClient is initialised at startup with the
Authorization header pre-set. All tool dispatchers call through this
module — no tool holds the raw api key.

Design decisions:
- httpx.AsyncClient for async compatibility with the MCP SDK event loop.
- Single shared instance: one TLS connection pool, not six.
- Explicit timeouts: 5s connect, 30s read/write/pool.
- No retries in Phase 1 (see architecture §9.3).
- Content uploads use bytes (UTF-8 encoded by caller) — never strings
  passed to the `data=` kwarg, which can trigger encoding surprises.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from typing import Any

import httpx

logger = logging.getLogger(__name__)

# Set once at startup by init(); used by all tool dispatchers.
_client: httpx.AsyncClient | None = None
_base_url: str = ""

# Timeout policy: 5s connect, 30s for the full exchange.
_TIMEOUT = httpx.Timeout(connect=5.0, read=30.0, write=30.0, pool=30.0)


@dataclass
class HttpResult:
    status: int
    body: bytes
    headers: dict[str, str]

    def json(self) -> Any:
        import json
        return json.loads(self.body.decode("utf-8"))

    @property
    def ok(self) -> bool:
        return 200 <= self.status < 300


def init(base_url: str, api_key: str) -> None:
    """Initialise the shared client. Call once at startup."""
    global _client, _base_url
    _base_url = base_url.rstrip("/")
    _client = httpx.AsyncClient(
        headers={"Authorization": f"Bearer {api_key}"},
        timeout=_TIMEOUT,
        follow_redirects=False,
    )
    logger.debug("HTTP client initialised: base_url=%s", _base_url)


async def close() -> None:
    """Close the shared client. Call on clean shutdown."""
    global _client
    if _client is not None:
        await _client.aclose()
        _client = None


def _assert_ready() -> httpx.AsyncClient:
    if _client is None:
        raise RuntimeError("HTTP client not initialised — call http_client.init() first.")
    return _client


async def get(path: str, params: dict[str, Any] | None = None) -> HttpResult:
    """
    GET {base_url}/{path} with optional query parameters.

    Multi-value parameters (e.g. type[]) must be passed as lists in the dict;
    httpx serialises them as repeated keys: ?type=task&type=documentation.
    """
    client = _assert_ready()
    url = f"{_base_url}/{path.lstrip('/')}"
    logger.debug("GET %s params=%s", url, params)
    try:
        resp = await client.get(url, params=params)
    except httpx.ConnectTimeout as exc:
        raise DiVoidUnreachable(f"Connect timeout reaching DiVoid: {exc}") from exc
    except httpx.TimeoutException as exc:
        raise DiVoidUnreachable(f"Timeout reaching DiVoid: {exc}") from exc
    except httpx.NetworkError as exc:
        raise DiVoidUnreachable(f"Network error reaching DiVoid: {exc}") from exc
    return HttpResult(
        status=resp.status_code,
        body=resp.content,
        headers=dict(resp.headers),
    )


async def post_json(path: str, body: Any) -> HttpResult:
    """POST JSON body to {base_url}/{path}."""
    import json
    client = _assert_ready()
    url = f"{_base_url}/{path.lstrip('/')}"
    encoded = json.dumps(body).encode("utf-8")
    logger.debug("POST %s body_len=%d", url, len(encoded))
    try:
        resp = await client.post(
            url,
            content=encoded,
            headers={"Content-Type": "application/json"},
        )
    except httpx.ConnectTimeout as exc:
        raise DiVoidUnreachable(f"Connect timeout reaching DiVoid: {exc}") from exc
    except httpx.TimeoutException as exc:
        raise DiVoidUnreachable(f"Timeout reaching DiVoid: {exc}") from exc
    except httpx.NetworkError as exc:
        raise DiVoidUnreachable(f"Network error reaching DiVoid: {exc}") from exc
    return HttpResult(
        status=resp.status_code,
        body=resp.content,
        headers=dict(resp.headers),
    )


async def post_bytes(path: str, body: bytes, content_type: str) -> HttpResult:
    """
    POST raw bytes to {base_url}/{path} with the specified Content-Type.

    This is the UTF-8-safe path for content uploads: the caller encodes
    the string to bytes before calling, so there is no shell or library
    re-encoding step that could mangle multibyte characters.
    """
    client = _assert_ready()
    url = f"{_base_url}/{path.lstrip('/')}"
    logger.debug("POST bytes %s content_type=%s body_len=%d", url, content_type, len(body))
    try:
        resp = await client.post(
            url,
            content=body,
            headers={"Content-Type": content_type},
        )
    except httpx.ConnectTimeout as exc:
        raise DiVoidUnreachable(f"Connect timeout reaching DiVoid: {exc}") from exc
    except httpx.TimeoutException as exc:
        raise DiVoidUnreachable(f"Timeout reaching DiVoid: {exc}") from exc
    except httpx.NetworkError as exc:
        raise DiVoidUnreachable(f"Network error reaching DiVoid: {exc}") from exc
    return HttpResult(
        status=resp.status_code,
        body=resp.content,
        headers=dict(resp.headers),
    )


async def patch_json(path: str, body: Any) -> HttpResult:
    """PATCH {base_url}/{path} with a JSON body (e.g. a JSON-Patch array)."""
    import json
    client = _assert_ready()
    url = f"{_base_url}/{path.lstrip('/')}"
    encoded = json.dumps(body).encode("utf-8")
    logger.debug("PATCH %s body_len=%d", url, len(encoded))
    try:
        resp = await client.patch(
            url,
            content=encoded,
            headers={"Content-Type": "application/json"},
        )
    except httpx.ConnectTimeout as exc:
        raise DiVoidUnreachable(f"Connect timeout reaching DiVoid: {exc}") from exc
    except httpx.TimeoutException as exc:
        raise DiVoidUnreachable(f"Timeout reaching DiVoid: {exc}") from exc
    except httpx.NetworkError as exc:
        raise DiVoidUnreachable(f"Network error reaching DiVoid: {exc}") from exc
    return HttpResult(
        status=resp.status_code,
        body=resp.content,
        headers=dict(resp.headers),
    )


async def delete(path: str) -> HttpResult:
    """DELETE {base_url}/{path}."""
    client = _assert_ready()
    url = f"{_base_url}/{path.lstrip('/')}"
    logger.debug("DELETE %s", url)
    try:
        resp = await client.delete(url)
    except httpx.ConnectTimeout as exc:
        raise DiVoidUnreachable(f"Connect timeout reaching DiVoid: {exc}") from exc
    except httpx.TimeoutException as exc:
        raise DiVoidUnreachable(f"Timeout reaching DiVoid: {exc}") from exc
    except httpx.NetworkError as exc:
        raise DiVoidUnreachable(f"Network error reaching DiVoid: {exc}") from exc
    return HttpResult(
        status=resp.status_code,
        body=resp.content,
        headers=dict(resp.headers),
    )


class DiVoidUnreachable(Exception):
    """Raised when the DiVoid API is not reachable (network / timeout)."""
