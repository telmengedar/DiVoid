/**
 * MSW request handlers for tests.
 *
 * These intercept fetch() calls at the network boundary and return
 * controlled fixtures, giving tests a stable API surface without
 * requiring a running backend.
 *
 * Add handlers as features are tested. Each feature's test file can
 * override individual handlers via server.use(...) for error cases.
 */

import { http, HttpResponse } from 'msw';
import type { Page, NodeDetails } from '@/types/divoid';

export const BASE_URL = 'http://localhost:5007/api';

// ─── Fixtures ─────────────────────────────────────────────────────────────────

/** Fixture: the default authenticated user returned by /api/users/me. */
export const defaultUser = {
  id: 1,
  name: 'Toni',
  email: 'toni@mamgo.io',
  enabled: true,
  createdAt: '2026-01-01T00:00:00Z',
  permissions: ['read', 'write', 'admin'],
};

/** Fixture: a sample node. */
export const sampleNode: NodeDetails = {
  id: 42,
  type: 'documentation',
  name: 'Test Document',
  status: 'open',
  contentType: 'text/markdown; charset=utf-8',
};

/** Fixture: a page of nodes for listing endpoints. */
export const samplePage: Page<NodeDetails> = {
  result: [
    { id: 1, type: 'task', name: 'First task', status: 'open' },
    { id: 2, type: 'documentation', name: 'Some doc', status: null },
    { id: 3, type: 'project', name: 'DiVoid', status: null },
  ],
  total: 3,
};

/** Fixture: a page with similarity scores (semantic search results). */
export const semanticPage: Page<NodeDetails> = {
  result: [
    { id: 10, type: 'documentation', name: 'Auth notes', status: null, similarity: 0.92 },
    { id: 11, type: 'task', name: 'Fix token refresh', status: 'open', similarity: 0.75 },
  ],
  total: 2,
};

// ─── Handlers ─────────────────────────────────────────────────────────────────

export const handlers = [
  // Auth
  http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(defaultUser)),

  // Node listing (bare)
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    const query = url.searchParams.get('query');
    const linkedto = url.searchParams.get('linkedto');

    if (query) {
      return HttpResponse.json(semanticPage);
    }
    if (linkedto) {
      return HttpResponse.json(samplePage);
    }
    return HttpResponse.json(samplePage);
  }),

  // Node detail
  http.get(`${BASE_URL}/nodes/:id`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 42) return HttpResponse.json(sampleNode);
    return HttpResponse.json({ code: 'notfound', text: `Node ${id} not found` }, { status: 404 });
  }),

  // Node content
  http.get(`${BASE_URL}/nodes/:id/content`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 42) {
      return new HttpResponse('# Hello\n\nThis is **markdown** content.', {
        headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
      });
    }
    return HttpResponse.json({ code: 'notfound', text: `Node ${id} not found` }, { status: 404 });
  }),
];
