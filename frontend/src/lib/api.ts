/**
 * DiVoid API client.
 *
 * Single responsibility: wrap fetch() with:
 *  - Bearer token injection (token comes from the OIDC context, not this module)
 *  - Query string serialisation (arrays → comma-separated per backend convention)
 *  - Typed DivoidApiError on non-2xx
 *  - 30s AbortController timeout
 *  - Dev-mode request logging
 *  - 401 reactive-refresh: single-flight signinSilent() → retry once → signinRedirect() fallback
 *
 * This module is PURE — no React hooks, no side effects beyond the fetch call.
 * Components and hooks use createApiClient() to get a bound client.
 *
 * Design: docs/architecture/frontend-bootstrap.md §6.3
 * Fix: DiVoid bug #403 v2 — concurrent-401 single-flight coalescing, synchronous dead flag.
 */

import { DivoidApiError } from '@/types/divoid';

// ─── Query string serialisation ───────────────────────────────────────────────

/**
 * Serialises an object to a query string compatible with the backend's
 * ArrayParameterBinderProvider: arrays become comma-separated values.
 *
 * Example: { type: ['task', 'project'], count: 20 } → "type=task,project&count=20"
 */
export function buildQueryString(params: Record<string, unknown>): string {
  const parts: string[] = [];

  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null) continue;
    if (Array.isArray(value)) {
      if (value.length === 0) continue;
      // Backend's ArrayParameterBinderProvider expects comma-separated values
      // without the comma being percent-encoded: type=task,project
      parts.push(
        `${encodeURIComponent(key)}=${value.map(String).join(',')}`,
      );
    } else {
      parts.push(`${encodeURIComponent(key)}=${encodeURIComponent(String(value))}`);
    }
  }

  return parts.join('&');
}

// ─── Error mapping ────────────────────────────────────────────────────────────

/**
 * Maps a non-2xx Response to a DivoidApiError.
 * Tries to parse the backend { code, text } JSON; falls back to status text.
 */
async function mapErrorResponse(response: Response): Promise<DivoidApiError> {
  const raw = await response.text().catch(() => '');
  let code = 'unknown';
  let text = response.statusText || `HTTP ${response.status}`;

  if (raw) {
    try {
      const parsed = JSON.parse(raw) as Record<string, unknown>;
      if (typeof parsed.code === 'string') code = parsed.code;
      if (typeof parsed.text === 'string') text = parsed.text;
    } catch {
      text = raw;
    }
  }

  return new DivoidApiError(response.status, code, text);
}

// ─── Client factory ───────────────────────────────────────────────────────────

/**
 * Creates a bound API client that injects the access token on every request.
 *
 * @param getToken        Synchronous token accessor (returns the current in-memory
 *                        token or undefined if unauthenticated).
 * @param signinSilent    Attempts a silent token refresh via the refresh-token grant.
 *                        Returns a Promise that resolves on success, rejects on failure.
 *                        When provided, a 401 triggers: single-flight silentRefresh → retry once.
 *                        If the retry also 401s, or if silentRefresh rejects, signinRedirect is
 *                        called exactly once. §6.3.
 * @param signinRedirect  Full Keycloak redirect — called exactly once after silent refresh
 *                        fails terminally. Subsequent callers short-circuit via the dead flag.
 * @param baseUrl         API base URL (defaults to VITE_API_BASE_URL env var). Accepts
 *                        an explicit value to allow tests to inject a test server URL
 *                        without mocking the environment.
 */
export function createApiClient(
  getToken: () => string | undefined,
  signinSilent?: () => Promise<unknown>,
  signinRedirect?: () => void,
  baseUrl?: string,
) {
  // Resolve base URL: explicit > env var > empty (will fail loudly at request time)
  const resolvedBase =
    baseUrl ??
    (import.meta.env.VITE_API_BASE_URL as string | undefined) ??
    '';

  // ─── Single-flight silent-refresh coalescing ───────────────────────────────
  //
  // Bug: with N concurrent 401s each called signinSilent() independently.
  // Result: N parallel token POSTs → all returned 400 invalid_grant → loop (550 reqs/44s).
  //
  // Fix: a single in-flight Promise is shared across all concurrent 401 waiters.
  // The first waiter starts signinSilent(); all others await the same Promise.
  // On success all N retry together. On rejection all N fail together and exactly
  // one redirect fires. See DiVoid bug #403 v2.
  let silentRefreshInFlight: Promise<unknown> | null = null;

  // Synchronous "session is dead" flag. Set to true the moment the single-flight
  // signinSilent() rejects or the retried request also 401s. All subsequent _fetch
  // calls read this BEFORE awaiting anything — no React render cycle, no race.
  // This is the fix for the async-setState race in the original bug #403 partial fix.
  let sessionDead = false;

  // Redirect-in-flight guard: ensures signinRedirect() is called at most once
  // per dead session, regardless of how many callers observe sessionDead=true.
  let redirectFired = false;

  /**
   * Fires the login redirect exactly once.
   * Idempotent: subsequent calls within the same dead session are no-ops.
   */
  function fireRedirectOnce(): void {
    if (signinRedirect && !redirectFired) {
      redirectFired = true;
      signinRedirect();
    }
  }

  /**
   * Core fetch wrapper.
   *
   * Accepts a `RequestInit`-like `init` (method, body, headers, signal) so that
   * both JSON and raw-body callers share the same §6.3 retry logic.
   *
   * @param isRetry  When true, skips the silent-refresh path so we never loop.
   */
  async function _fetch(
    path: string,
    init: {
      method: string;
      body?: BodyInit;
      headers?: Record<string, string>;
      signal?: AbortSignal;
    },
    isRetry = false,
  ): Promise<Response> {
    // Short-circuit synchronously: once the session is dead, stop trying to refresh.
    // This check happens before any await, so every concurrent caller observes the
    // flag in the same microtask tick — no React state round-trip required.
    if (sessionDead && !isRetry) {
      fireRedirectOnce();
      // Return a synthetic 401 so callers throw DivoidApiError(401) fast.
      return new Response(JSON.stringify({ code: 'unauthorized', text: 'Session expired' }), {
        status: 401,
        headers: { 'Content-Type': 'application/json' },
      });
    }

    const token = getToken();

    const headers: Record<string, string> = { ...(init.headers ?? {}) };
    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    }

    // 30-second client-side timeout — guards against hanging path queries.
    const timeoutController = new AbortController();
    const timeoutId = setTimeout(() => timeoutController.abort(), 30_000);

    // Combine caller signal with our timeout signal if supported
    let combinedSignal: AbortSignal;
    if (init.signal && typeof AbortSignal.any === 'function') {
      combinedSignal = AbortSignal.any([init.signal, timeoutController.signal]);
    } else {
      combinedSignal = timeoutController.signal;
    }

    if (import.meta.env.DEV) {
      console.debug(`[DiVoid API] ${init.method} ${path}`);
    }

    let response: Response;
    try {
      response = await fetch(`${resolvedBase}${path}`, {
        method: init.method,
        headers,
        body: init.body,
        signal: combinedSignal,
      });
    } finally {
      clearTimeout(timeoutId);
    }

    if (response.status === 401 && !isRetry && signinSilent) {
      // §6.3 single-flight: coalesce all concurrent 401 waiters onto one signinSilent() call.
      // If no refresh is in flight, start one. Otherwise await the one already running.
      if (!silentRefreshInFlight) {
        silentRefreshInFlight = signinSilent().finally(() => {
          silentRefreshInFlight = null;
        });
      }

      try {
        await silentRefreshInFlight;
      } catch {
        // Single-flight signinSilent rejected — session is terminally dead.
        // Mark synchronously so every concurrent waiter that reaches this point
        // will also short-circuit on their next _fetch call.
        sessionDead = true;
        fireRedirectOnce();
        return response; // caller will see the 401 and throw DivoidApiError
      }

      // Retry once with the freshly-obtained token.
      const retried = await _fetch(path, init, true);
      if (retried.status === 401) {
        // Retry also failed — session dead, redirect once.
        sessionDead = true;
        fireRedirectOnce();
      }
      return retried;
    }

    return response;
  }

  async function request<T>(
    method: string,
    path: string,
    body?: unknown,
    signal?: AbortSignal,
  ): Promise<T> {
    const headers: Record<string, string> = {};
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
    }
    const response = await _fetch(path, {
      method,
      body: body !== undefined ? JSON.stringify(body) : undefined,
      headers,
      signal,
    });

    if (!response.ok) {
      const error = await mapErrorResponse(response);
      throw error;
    }

    if (response.status === 204) {
      return undefined as T;
    }

    const text = await response.text();
    if (!text) {
      return undefined as T;
    }

    return JSON.parse(text) as T;
  }

  return {
    get<T>(
      path: string,
      params?: Record<string, unknown>,
      signal?: AbortSignal,
    ): Promise<T> {
      if (params) {
        const qs = buildQueryString(params);
        const sep = path.includes('?') ? '&' : '?';
        return request<T>('GET', qs ? `${path}${sep}${qs}` : path, undefined, signal);
      }
      return request<T>('GET', path, undefined, signal);
    },

    post<T>(path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
      return request<T>('POST', path, body, signal);
    },

    patch<T>(path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
      return request<T>('PATCH', path, body, signal);
    },

    delete<T = void>(path: string, signal?: AbortSignal): Promise<T> {
      return request<T>('DELETE', path, undefined, signal);
    },

    /**
     * Fetches a URL and returns the raw Response (no JSON parsing).
     * Inherits all client guarantees: Bearer token, 30s timeout, combined signal,
     * dev logging, and the §6.3 silent-refresh-then-redirect 401 chain.
     *
     * The caller is responsible for reading the response body and interpreting the
     * Content-Type. Throws DivoidApiError on non-2xx (after the retry path).
     */
    async fetchRaw(path: string, signal?: AbortSignal): Promise<Response> {
      const response = await _fetch(path, { method: 'GET', signal });
      if (!response.ok) {
        const error = await mapErrorResponse(response);
        throw error;
      }
      return response;
    },

    /**
     * POSTs a raw body with a caller-supplied Content-Type, returning the raw Response.
     * Inherits all client guarantees: Bearer token, 30s timeout, combined signal,
     * dev logging, and the §6.3 silent-refresh-then-redirect 401 chain.
     *
     * Use this instead of post() when the body is not JSON (e.g. file upload,
     * raw bytes, markdown text). The BodyInit type covers Uint8Array, Blob, File,
     * FormData, ArrayBuffer, and string.
     *
     * Throws DivoidApiError on non-2xx (after the retry path).
     */
    async postRaw(
      path: string,
      body: BodyInit,
      contentType: string,
      signal?: AbortSignal,
    ): Promise<Response> {
      const response = await _fetch(path, {
        method: 'POST',
        body,
        headers: { 'Content-Type': contentType },
        signal,
      });
      if (!response.ok) {
        const error = await mapErrorResponse(response);
        throw error;
      }
      return response;
    },
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
