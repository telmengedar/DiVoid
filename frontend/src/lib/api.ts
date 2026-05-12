/**
 * DiVoid API client.
 *
 * Single responsibility: wrap fetch() with:
 *  - Bearer token injection (token comes from the OIDC context, not this module)
 *  - Query string serialisation (arrays → comma-separated per backend convention)
 *  - Typed DivoidApiError on non-2xx
 *  - 30s AbortController timeout
 *  - Dev-mode request logging
 *  - 401 reactive-refresh: signinSilent() → retry once → signinRedirect() fallback
 *
 * This module is PURE — no React hooks, no side effects beyond the fetch call.
 * Components and hooks use createApiClient() to get a bound client.
 *
 * Design: docs/architecture/frontend-bootstrap.md §6.3
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
 *                        When provided, a 401 triggers: silentRefresh → retry once.
 *                        If the retry also 401s, or if silentRefresh rejects,
 *                        signinRedirect is called. Max 1 refresh per request. §6.3.
 * @param signinRedirect  Full Keycloak redirect — called only after silent refresh
 *                        fails or the retry returns 401 again.
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

  /**
   * Core fetch wrapper.
   *
   * @param isRetry  When true, skips the silent-refresh path so we never loop.
   */
  async function _fetch(
    method: string,
    path: string,
    body?: unknown,
    signal?: AbortSignal,
    isRetry = false,
  ): Promise<Response> {
    const token = getToken();

    const headers: Record<string, string> = {};
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
    }
    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    }

    // 30-second client-side timeout — guards against hanging path queries.
    const timeoutController = new AbortController();
    const timeoutId = setTimeout(() => timeoutController.abort(), 30_000);

    // Combine caller signal with our timeout signal if supported
    let combinedSignal: AbortSignal;
    if (signal && typeof AbortSignal.any === 'function') {
      combinedSignal = AbortSignal.any([signal, timeoutController.signal]);
    } else {
      combinedSignal = timeoutController.signal;
    }

    if (import.meta.env.DEV) {
      console.debug(`[DiVoid API] ${method} ${path}`);
    }

    let response: Response;
    try {
      response = await fetch(`${resolvedBase}${path}`, {
        method,
        headers,
        body: body !== undefined ? JSON.stringify(body) : undefined,
        signal: combinedSignal,
      });
    } finally {
      clearTimeout(timeoutId);
    }

    if (response.status === 401 && !isRetry && signinSilent) {
      // §6.3: attempt silent refresh, then retry the original request once.
      try {
        await signinSilent();
      } catch {
        // Silent refresh failed (refresh token expired / revoked).
        if (signinRedirect) signinRedirect();
        return response; // caller will see the 401 and throw
      }

      // Retry once with the freshly-obtained token.
      const retried = await _fetch(method, path, body, signal, true);
      if (retried.status === 401) {
        // Retry also failed — fall through to full redirect.
        if (signinRedirect) signinRedirect();
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
    const response = await _fetch(method, path, body, signal);

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
      const response = await _fetch('GET', path, undefined, signal);
      if (!response.ok) {
        const error = await mapErrorResponse(response);
        throw error;
      }
      return response;
    },
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
