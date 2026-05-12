/**
 * MSW server for Vitest (Node/jsdom environment).
 *
 * Import and start in test files that make API calls:
 *
 *   import { server } from '@/test/msw/server';
 *   beforeAll(() => server.listen());
 *   afterEach(() => server.resetHandlers());
 *   afterAll(() => server.close());
 *
 * Or override handlers in individual tests:
 *
 *   server.use(http.get('http://localhost:5007/api/users/me', () => HttpResponse.json({}, { status: 401 })));
 */

import { setupServer } from 'msw/node';
import { handlers } from './handlers';

export const server = setupServer(...handlers);
