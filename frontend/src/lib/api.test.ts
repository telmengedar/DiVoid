/**
 * Tests for lib/api.ts — the Bearer-injecting fetch wrapper.
 *
 * Verifies:
 *  - Token is injected on every request
 *  - Query strings are serialised correctly (arrays → comma-separated)
 *  - DivoidApiError is thrown on non-2xx responses
 *  - 401 reactive-refresh contract (§6.3):
 *      · signinSilent() called on 401; retry succeeds → no redirect, no error
 *      · signinSilent() rejects → signinRedirect() called exactly once
 *      · retry also 401s → signinRedirect() called (no infinite loop)
 *  - fetchRaw returns the Response on success; throws DivoidApiError on error
 *  - postRaw POSTs a raw body with caller-supplied Content-Type; inherits §6.3 chain
 *  - 204 No Content returns undefined cleanly
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { createApiClient, buildQueryString } from './api';

// ─── Test server ──────────────────────────────────────────────────────────────

const BASE_URL = 'http://localhost:5007/api';

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, ({ request }) => {
    const auth = request.headers.get('Authorization');
    if (!auth?.startsWith('Bearer ')) {
      return HttpResponse.json(
        { code: 'unauthorized', text: 'Missing token' },
        { status: 401 },
      );
    }
    return HttpResponse.json({
      id: 1,
      name: 'Toni',
      email: 'toni@mamgo.io',
      enabled: true,
      createdAt: '2026-01-01T00:00:00Z',
      permissions: ['read'],
    });
  }),
  http.get(`${BASE_URL}/nodes`, () => {
    return HttpResponse.json({ result: [], total: 0 });
  }),
  http.delete(`${BASE_URL}/nodes/42`, () => {
    return new HttpResponse(null, { status: 204 });
  }),
  http.get(`${BASE_URL}/nodes/99/content`, ({ request }) => {
    const auth = request.headers.get('Authorization');
    if (!auth?.startsWith('Bearer ')) {
      return HttpResponse.json(
        { code: 'unauthorized', text: 'Missing token' },
        { status: 401 },
      );
    }
    return new HttpResponse('hello world', { status: 200, headers: { 'Content-Type': 'text/plain' } });
  }),
  http.post(`${BASE_URL}/nodes/99/content`, ({ request }) => {
    const auth = request.headers.get('Authorization');
    if (!auth?.startsWith('Bearer ')) {
      return HttpResponse.json(
        { code: 'unauthorized', text: 'Missing token' },
        { status: 401 },
      );
    }
    return new HttpResponse(null, { status: 204 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

// ─── buildQueryString ─────────────────────────────────────────────────────────

describe('buildQueryString', () => {
  it('serialises scalar values', () => {
    expect(buildQueryString({ count: 20, sort: 'name' })).toBe('count=20&sort=name');
  });

  it('serialises arrays as comma-separated values', () => {
    // Backend ArrayParameterBinderProvider expects type=task,project (comma unencoded)
    expect(buildQueryString({ type: ['task', 'project'] })).toBe('type=task,project');
  });

  it('omits undefined and null values', () => {
    expect(buildQueryString({ a: undefined, b: null, c: 'x' })).toBe('c=x');
  });

  it('omits empty arrays', () => {
    expect(buildQueryString({ ids: [] })).toBe('');
  });

  it('encodes special characters in values', () => {
    const qs = buildQueryString({ name: 'hello world' });
    expect(qs).toBe('name=hello%20world');
  });
});

// ─── createApiClient — basic behaviour ────────────────────────────────────────

describe('createApiClient', () => {
  it('injects the Bearer token on GET requests', async () => {
    const client = createApiClient(() => 'test-token', undefined, undefined, BASE_URL);
    const result = await client.get<{ name: string }>('/users/me');
    expect(result.name).toBe('Toni');
  });

  it('throws DivoidApiError with code and text on 401 (no signinSilent provided)', async () => {
    // No token, no silent handler → should throw immediately
    const client = createApiClient(() => undefined, undefined, undefined, BASE_URL);
    await expect(client.get('/users/me')).rejects.toMatchObject({
      name: 'DivoidApiError',
      status: 401,
      code: 'unauthorized',
    });
  });

  it('returns undefined cleanly on 204 No Content', async () => {
    const client = createApiClient(() => 'test-token', undefined, undefined, BASE_URL);
    const result = await client.delete('/nodes/42');
    expect(result).toBeUndefined();
  });

  it('passes query params as a query string', async () => {
    let capturedUrl: string | undefined;
    server.use(
      http.get(`${BASE_URL}/nodes`, ({ request }) => {
        capturedUrl = request.url;
        return HttpResponse.json({ result: [], total: 0 });
      }),
    );

    const client = createApiClient(() => 'test-token', undefined, undefined, BASE_URL);
    await client.get('/nodes', { type: ['task'], count: 10 });

    expect(capturedUrl).toContain('type=task');
    expect(capturedUrl).toContain('count=10');
  });
});

// ─── createApiClient — §6.3 reactive-401 contract ────────────────────────────

describe('createApiClient — reactive 401 retry (§6.3)', () => {
  it('calls signinSilent on 401, retries with new token, does not call signinRedirect', async () => {
    // First call: no token → 401.
    // After signinSilent resolves, getToken now returns 'new-token' → 200.
    let callCount = 0;
    server.use(
      http.get(`${BASE_URL}/users/me`, ({ request }) => {
        callCount++;
        const auth = request.headers.get('Authorization');
        if (auth === 'Bearer new-token') {
          return HttpResponse.json({ id: 1, name: 'Toni' });
        }
        return HttpResponse.json({ code: 'unauthorized', text: 'bad' }, { status: 401 });
      }),
    );

    let tokenValue: string | undefined = undefined;
    const signinSilent = vi.fn(async () => { tokenValue = 'new-token'; });
    const signinRedirect = vi.fn();
    const client = createApiClient(() => tokenValue, signinSilent, signinRedirect, BASE_URL);

    const result = await client.get<{ name: string }>('/users/me');

    expect(result.name).toBe('Toni');
    expect(signinSilent).toHaveBeenCalledOnce();
    expect(signinRedirect).not.toHaveBeenCalled();
    // Should have made exactly 2 fetch calls: the original + the retry
    expect(callCount).toBe(2);
  });

  it('calls signinRedirect when signinSilent rejects (refresh token expired)', async () => {
    server.use(
      http.get(`${BASE_URL}/users/me`, () =>
        HttpResponse.json({ code: 'unauthorized', text: 'expired' }, { status: 401 }),
      ),
    );

    const signinSilent = vi.fn(async () => { throw new Error('refresh failed'); });
    const signinRedirect = vi.fn();
    const client = createApiClient(() => undefined, signinSilent, signinRedirect, BASE_URL);

    await expect(client.get('/users/me')).rejects.toMatchObject({ status: 401 });

    expect(signinSilent).toHaveBeenCalledOnce();
    expect(signinRedirect).toHaveBeenCalledOnce();
  });

  it('calls signinRedirect after retry also returns 401 (no-loop guarantee)', async () => {
    // Server always returns 401, even after signinSilent resolves.
    let callCount = 0;
    server.use(
      http.get(`${BASE_URL}/users/me`, () => {
        callCount++;
        return HttpResponse.json({ code: 'unauthorized', text: 'still bad' }, { status: 401 });
      }),
    );

    const signinSilent = vi.fn(async () => { /* resolves but token is still rejected */ });
    const signinRedirect = vi.fn();
    const client = createApiClient(() => undefined, signinSilent, signinRedirect, BASE_URL);

    await expect(client.get('/users/me')).rejects.toMatchObject({ status: 401 });

    expect(signinSilent).toHaveBeenCalledOnce();
    expect(signinRedirect).toHaveBeenCalledOnce();
    // Exactly 2 fetches: original + one retry. No third attempt.
    expect(callCount).toBe(2);
  });
});

// ─── createApiClient — single-flight & dead-session (bug #403 v2) ─────────────
//
// These tests verify the three-part fix:
//   1. Concurrent 401s share ONE signinSilent() call (single-flight coalescing).
//   2. After signinSilent rejects, signinRedirect() fires exactly once, regardless
//      of how many concurrent callers observed the 401.
//   3. After terminal failure, subsequent _fetch calls short-circuit synchronously:
//      no signinSilent, no HTTP request to the token endpoint, DivoidApiError(401) fast.
//
// Negative proof:
//   Test 1: remove `if (!silentRefreshInFlight)` guard in api.ts — each concurrent
//           401 starts its own Promise → signinSilent called N times. Test fails.
//   Test 2: remove `redirectFired` guard and call signinRedirect() inside the
//           single-flight catch instead — N concurrent waiters each fire their own
//           redirect. Test fails with signinRedirect called > 1 time.
//   Test 3: remove `if (sessionDead && !isRetry)` short-circuit at the top of _fetch
//           → subsequent callers reach the network and trigger more signinSilent calls.
//           Test fails with signinSilent called again.

describe('createApiClient — single-flight & dead-session (§6.3 v2)', () => {
  it('concurrent 401s share exactly one signinSilent() call (single-flight coalescing)', async () => {
    // Server always 401s — simulates dead refresh token.
    let fetchCallCount = 0;
    server.use(
      http.get(`${BASE_URL}/users/me`, () => {
        fetchCallCount++;
        return HttpResponse.json({ code: 'unauthorized', text: 'expired' }, { status: 401 });
      }),
    );

    // signinSilent resolves (pretend it succeeded) so concurrent waiters can
    // proceed to the retry path — we just want to measure call count.
    const signinSilent = vi.fn(async () => { /* resolves */ });
    const signinRedirect = vi.fn();
    const client = createApiClient(() => undefined, signinSilent, signinRedirect, BASE_URL);

    // Fire 6 concurrent requests — same N as the HAR evidence.
    const results = await Promise.allSettled(
      Array.from({ length: 6 }, () => client.get('/users/me')),
    );

    // All 6 should have rejected (retry also 401s).
    expect(results.every((r) => r.status === 'rejected')).toBe(true);

    // KEY: signinSilent called exactly once — all 6 awaited the same Promise.
    expect(signinSilent).toHaveBeenCalledOnce();
  });

  it('failed silent refresh fires signinRedirect() exactly once across N concurrent 401s', async () => {
    server.use(
      http.get(`${BASE_URL}/users/me`, () =>
        HttpResponse.json({ code: 'unauthorized', text: 'expired' }, { status: 401 }),
      ),
    );

    // signinSilent rejects — simulates invalid_grant from Keycloak.
    const signinSilent = vi.fn(async () => { throw new Error('invalid_grant'); });
    const signinRedirect = vi.fn();
    const client = createApiClient(() => undefined, signinSilent, signinRedirect, BASE_URL);

    // Fire 6 concurrent requests — each will 401 and await the single-flight Promise.
    await Promise.allSettled(
      Array.from({ length: 6 }, () => client.get('/users/me')),
    );

    // KEY: signinRedirect called exactly once — the redirectFired guard suppressed the rest.
    expect(signinRedirect).toHaveBeenCalledOnce();
    // Single-flight: signinSilent called exactly once too.
    expect(signinSilent).toHaveBeenCalledOnce();
  });

  it('after terminal failure subsequent _fetch calls reject without calling signinSilent again', async () => {
    server.use(
      http.get(`${BASE_URL}/users/me`, () =>
        HttpResponse.json({ code: 'unauthorized', text: 'expired' }, { status: 401 }),
      ),
    );

    const signinSilent = vi.fn(async () => { throw new Error('invalid_grant'); });
    const signinRedirect = vi.fn();
    const client = createApiClient(() => undefined, signinSilent, signinRedirect, BASE_URL);

    // First wave: cause the terminal failure.
    await client.get('/users/me').catch(() => {});

    // signinSilent was called once for the first failure.
    expect(signinSilent).toHaveBeenCalledOnce();
    vi.clearAllMocks();

    // Second wave: sessionDead is now true; these must short-circuit before
    // reaching the network or calling signinSilent.
    const results = await Promise.allSettled([
      client.get('/users/me'),
      client.get('/users/me'),
      client.get('/users/me'),
    ]);

    // All rejected with DivoidApiError(401).
    expect(results.every((r) => r.status === 'rejected')).toBe(true);
    for (const r of results) {
      if (r.status === 'rejected') {
        expect((r.reason as { status?: number }).status).toBe(401);
      }
    }

    // KEY: signinSilent NOT called again after terminal failure.
    expect(signinSilent).not.toHaveBeenCalled();
  });

  it('after terminal failure redirectFired guard ensures signinRedirect not called again', async () => {
    server.use(
      http.get(`${BASE_URL}/users/me`, () =>
        HttpResponse.json({ code: 'unauthorized', text: 'expired' }, { status: 401 }),
      ),
    );

    const signinSilent = vi.fn(async () => { throw new Error('invalid_grant'); });
    const signinRedirect = vi.fn();
    const client = createApiClient(() => undefined, signinSilent, signinRedirect, BASE_URL);

    // First call: triggers terminal failure + redirect.
    await client.get('/users/me').catch(() => {});
    expect(signinRedirect).toHaveBeenCalledOnce();

    const callsAfterFirst = signinRedirect.mock.calls.length;

    // Subsequent calls: short-circuit path still calls fireRedirectOnce() but
    // it must be a no-op since redirectFired=true.
    await client.get('/users/me').catch(() => {});
    await client.get('/users/me').catch(() => {});

    // signinRedirect call count must not increase beyond the first call.
    expect(signinRedirect).toHaveBeenCalledTimes(callsAfterFirst);
  });
});

// ─── createApiClient — fetchRaw ────────────────────────────────────────────────

describe('createApiClient.fetchRaw', () => {
  it('returns raw Response on success', async () => {
    const client = createApiClient(() => 'test-token', undefined, undefined, BASE_URL);
    const response = await client.fetchRaw('/nodes/99/content');
    expect(response.ok).toBe(true);
    const text = await response.text();
    expect(text).toBe('hello world');
  });

  it('throws DivoidApiError on 401 (no signinSilent)', async () => {
    const client = createApiClient(() => undefined, undefined, undefined, BASE_URL);
    await expect(client.fetchRaw('/nodes/99/content')).rejects.toMatchObject({
      name: 'DivoidApiError',
      status: 401,
    });
  });

  it('calls signinSilent on 401, retries, returns Response on retry success', async () => {
    let tokenValue: string | undefined = undefined;
    const signinSilent = vi.fn(async () => { tokenValue = 'test-token'; });
    const signinRedirect = vi.fn();
    const client = createApiClient(() => tokenValue, signinSilent, signinRedirect, BASE_URL);

    const response = await client.fetchRaw('/nodes/99/content');
    expect(response.ok).toBe(true);
    expect(signinSilent).toHaveBeenCalledOnce();
    expect(signinRedirect).not.toHaveBeenCalled();
  });

  it('calls signinRedirect when signinSilent rejects on fetchRaw path', async () => {
    const signinSilent = vi.fn(async () => { throw new Error('refresh failed'); });
    const signinRedirect = vi.fn();
    const client = createApiClient(() => undefined, signinSilent, signinRedirect, BASE_URL);

    await expect(client.fetchRaw('/nodes/99/content')).rejects.toMatchObject({ status: 401 });
    expect(signinRedirect).toHaveBeenCalledOnce();
  });

  it('calls signinRedirect when fetchRaw retry also 401s (no-loop guarantee)', async () => {
    let callCount = 0;
    server.use(
      http.get(`${BASE_URL}/nodes/99/content`, () => {
        callCount++;
        return HttpResponse.json({ code: 'unauthorized', text: 'still bad' }, { status: 401 });
      }),
    );

    const signinSilent = vi.fn(async () => { /* resolves but server still rejects */ });
    const signinRedirect = vi.fn();
    const client = createApiClient(() => undefined, signinSilent, signinRedirect, BASE_URL);

    await expect(client.fetchRaw('/nodes/99/content')).rejects.toMatchObject({ status: 401 });
    expect(signinSilent).toHaveBeenCalledOnce();
    expect(signinRedirect).toHaveBeenCalledOnce();
    expect(callCount).toBe(2);
  });
});

// ─── createApiClient — postRaw ─────────────────────────────────────────────────

describe('createApiClient.postRaw', () => {
  it('returns raw Response on success (200)', async () => {
    const client = createApiClient(() => 'test-token', undefined, undefined, BASE_URL);
    const response = await client.postRaw('/nodes/99/content', 'hello', 'text/plain');
    expect(response.status).toBe(204);
    expect(response.ok).toBe(true);
  });

  it('injects Bearer token and caller-supplied Content-Type', async () => {
    let capturedAuth: string | null = null;
    let capturedContentType: string | null = null;
    server.use(
      http.post(`${BASE_URL}/nodes/99/content`, ({ request }) => {
        capturedAuth = request.headers.get('Authorization');
        capturedContentType = request.headers.get('Content-Type');
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const client = createApiClient(() => 'my-token', undefined, undefined, BASE_URL);
    await client.postRaw('/nodes/99/content', 'data', 'text/markdown; charset=utf-8');

    expect(capturedAuth).toBe('Bearer my-token');
    expect(capturedContentType).toBe('text/markdown; charset=utf-8');
  });

  it('throws DivoidApiError on 401 (no signinSilent)', async () => {
    const client = createApiClient(() => undefined, undefined, undefined, BASE_URL);
    await expect(
      client.postRaw('/nodes/99/content', 'data', 'text/plain'),
    ).rejects.toMatchObject({ name: 'DivoidApiError', status: 401 });
  });

  it('§6.3: signinSilent called on 401; retry succeeds; signinRedirect NOT called', async () => {
    let callCount = 0;
    server.use(
      http.post(`${BASE_URL}/nodes/99/content`, ({ request }) => {
        callCount++;
        const auth = request.headers.get('Authorization');
        if (auth === 'Bearer new-token') {
          return new HttpResponse(null, { status: 204 });
        }
        return HttpResponse.json({ code: 'unauthorized', text: 'bad' }, { status: 401 });
      }),
    );

    let tokenValue: string | undefined = undefined;
    const signinSilent = vi.fn(async () => { tokenValue = 'new-token'; });
    const signinRedirect = vi.fn();
    const client = createApiClient(() => tokenValue, signinSilent, signinRedirect, BASE_URL);

    const response = await client.postRaw('/nodes/99/content', 'data', 'text/plain');
    expect(response.ok).toBe(true);
    expect(signinSilent).toHaveBeenCalledOnce();
    expect(signinRedirect).not.toHaveBeenCalled();
    expect(callCount).toBe(2);
  });

  it('§6.3: signinRedirect called when signinSilent rejects', async () => {
    server.use(
      http.post(`${BASE_URL}/nodes/99/content`, () =>
        HttpResponse.json({ code: 'unauthorized', text: 'expired' }, { status: 401 }),
      ),
    );

    const signinSilent = vi.fn(async () => { throw new Error('refresh failed'); });
    const signinRedirect = vi.fn();
    const client = createApiClient(() => undefined, signinSilent, signinRedirect, BASE_URL);

    await expect(
      client.postRaw('/nodes/99/content', 'data', 'text/plain'),
    ).rejects.toMatchObject({ status: 401 });

    expect(signinSilent).toHaveBeenCalledOnce();
    expect(signinRedirect).toHaveBeenCalledOnce();
  });

  it('§6.3: signinRedirect called after retry also 401s (no-loop guarantee)', async () => {
    let callCount = 0;
    server.use(
      http.post(`${BASE_URL}/nodes/99/content`, () => {
        callCount++;
        return HttpResponse.json({ code: 'unauthorized', text: 'still bad' }, { status: 401 });
      }),
    );

    const signinSilent = vi.fn(async () => { /* resolves but server still rejects */ });
    const signinRedirect = vi.fn();
    const client = createApiClient(() => undefined, signinSilent, signinRedirect, BASE_URL);

    await expect(
      client.postRaw('/nodes/99/content', 'data', 'text/plain'),
    ).rejects.toMatchObject({ status: 401 });
    expect(signinSilent).toHaveBeenCalledOnce();
    expect(signinRedirect).toHaveBeenCalledOnce();
    // Exactly 2 fetches: original + one retry. No third attempt.
    expect(callCount).toBe(2);
  });
});
