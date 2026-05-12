/**
 * Tests for lib/api.ts — the Bearer-injecting fetch wrapper.
 *
 * Verifies:
 *  - Token is injected on every request
 *  - Query strings are serialised correctly (arrays → comma-separated)
 *  - DivoidApiError is thrown on non-2xx responses
 *  - 401 triggers the onUnauthorized callback
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

// ─── createApiClient ──────────────────────────────────────────────────────────

describe('createApiClient', () => {
  it('injects the Bearer token on GET requests', async () => {
    const client = createApiClient(() => 'test-token', undefined, BASE_URL);
    const result = await client.get<{ name: string }>('/users/me');
    expect(result.name).toBe('Toni');
  });

  it('throws DivoidApiError with code and text on 401', async () => {
    // No token → server returns 401
    const client = createApiClient(() => undefined, undefined, BASE_URL);
    await expect(client.get('/users/me')).rejects.toMatchObject({
      name: 'DivoidApiError',
      status: 401,
      code: 'unauthorized',
    });
  });

  it('calls onUnauthorized when a 401 is received', async () => {
    const onUnauthorized = vi.fn();
    const client = createApiClient(() => undefined, onUnauthorized, BASE_URL);
    await expect(client.get('/users/me')).rejects.toThrow();
    expect(onUnauthorized).toHaveBeenCalledOnce();
  });

  it('returns undefined cleanly on 204 No Content', async () => {
    const client = createApiClient(() => 'test-token', undefined, BASE_URL);
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

    const client = createApiClient(() => 'test-token', undefined, BASE_URL);
    await client.get('/nodes', { type: ['task'], count: 10 });

    expect(capturedUrl).toContain('type=task');
    expect(capturedUrl).toContain('count=10');
  });
});
