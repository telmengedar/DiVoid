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

const BASE_URL = 'http://localhost:5007/api';

/** Fixture: the default authenticated user returned by /api/users/me. */
export const defaultUser = {
  id: 1,
  name: 'Toni',
  email: 'toni@mamgo.io',
  enabled: true,
  createdAt: '2026-01-01T00:00:00Z',
  permissions: ['read', 'write', 'admin'],
};

export const handlers = [
  http.get(`${BASE_URL}/users/me`, () => {
    return HttpResponse.json(defaultUser);
  }),
];
