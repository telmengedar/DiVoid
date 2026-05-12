/**
 * DiVoid API client.
 *
 * Single responsibility: wrap fetch() with:
 *  - Bearer token injection (token comes from the OIDC context, not this module)
 *  - Query string serialisation (arrays → comma-separated per backend convention)
 *  - Typed DivoidApiError on non-2xx
 *  - 30s AbortController timeout
 *  - Dev-mode request logging
 *
 * This module is PURE — no React hooks, no side effects beyond the fetch call.
 * Components and hooks use createApiClient() to get a bound client.
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

/** Callback type for 401 notification — used by auth context to trigger refresh. */
export type OnUnauthorized = () => void;

/**
 * Creates a bound API client that injects the access token on every request.
 *
 * @param getToken  Synchronous token accessor (returns the current in-memory token
 *                  or undefined if unauthenticated). Callers should gate calls on
 *                  authentication status before calling the client.
 * @param onUnauthorized  Called when a 401 is received. The auth context should
 *                        attempt a silent refresh and retry.
 * @param baseUrl   API base URL (defaults to VITE_API_BASE_URL env var). Accepts
 *                  an explicit value to allow tests to inject a test server URL
 *                  without mocking the environment.
 */
export function createApiClient(
  getToken: () => string | undefined,
  onUnauthorized?: OnUnauthorized,
  baseUrl?: string,
) {
  // Resolve base URL: explicit > env var > empty (will fail loudly at request time)
  const resolvedBase =
    baseUrl ??
    (import.meta.env.VITE_API_BASE_URL as string | undefined) ??
    '';

  async function request<T>(
    method: string,
    path: string,
    body?: unknown,
    signal?: AbortSignal,
  ): Promise<T> {
    const token = getToken();

    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
    };
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

    if (!response.ok) {
      const error = await mapErrorResponse(response);

      if (response.status === 401 && onUnauthorized) {
        onUnauthorized();
      }

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
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
