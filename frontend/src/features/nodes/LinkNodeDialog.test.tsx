/**
 * Tests for LinkNodeDialog.
 *
 * Covers:
 *  - Renders the link-type selector (defaulted to "None") and the optional
 *    context field alongside the existing semantic-search picker (DiVoid #7142).
 *  - Confirming with the default None/no-context sends no query string on the
 *    POST — byte-identical to the pre-#7142 request.
 *  - Confirming a chosen link type + context sends both as query params while
 *    the POST body stays the bare target id.
 *  - Error: mutation error does not close the dialog.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { type ReactNode } from 'react';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL, semanticPage } from '@/test/msw/handlers';
import { LinkNodeDialog } from './LinkNodeDialog';

let lastLinkUrl: URL | undefined;
let lastLinkBody: unknown;

const server = setupServer(
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    if (url.searchParams.get('query')) return HttpResponse.json(semanticPage);
    return HttpResponse.json({ result: [], total: 0 });
  }),
  http.post(`${BASE_URL}/nodes/:id/links`, async ({ request }) => {
    lastLinkUrl = new URL(request.url);
    lastLinkBody = await request.json();
    return new HttpResponse(null, { status: 204 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  lastLinkUrl = undefined;
  lastLinkBody = undefined;
});
afterAll(() => server.close());

vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(() => ({
    isAuthenticated: true,
    user: { access_token: 'test-token' },
    signinRedirect: vi.fn(),
    signinSilent: vi.fn().mockResolvedValue(undefined),
  })),
}));

vi.mock('@/lib/constants', () => ({
  API_BASE_URL: BASE_URL,
  API: {
    USERS: { ME: '/users/me' },
    NODES: {
      LIST: '/nodes',
      DETAIL: (id: number) => `/nodes/${id}`,
      CONTENT: (id: number) => `/nodes/${id}/content`,
      LINKS: (id: number) => `/nodes/${id}/links`,
      UNLINK: (s: number, t: number) => `/nodes/${s}/links/${t}`,
    },
    HEALTH: '/health',
  },
  ROUTES: {
    HOME: '/',
    CALLBACK: '/callback',
    LOGOUT: '/logout',
    SEARCH: '/search',
    NODE_DETAIL: (id: number) => `/nodes/${id}`,
    WORKSPACE: '/workspace',
    TASKS: '/tasks',
    PROJECT_TASKS: (id: number) => `/tasks/${id}`,
  },
}));

vi.mock('sonner', () => ({ toast: { error: vi.fn(), info: vi.fn(), success: vi.fn() } }));

function Wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

async function selectFirstResult(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText(/search for a node to link/i), 'auth');
  await user.click(screen.getByRole('button', { name: /search/i }));
  const option = await screen.findByRole('option', { name: /auth notes/i });
  await user.click(option);
}

describe('LinkNodeDialog', () => {
  it('renders the link-type selector defaulted to None and an empty optional context field', () => {
    render(
      <Wrapper>
        <LinkNodeDialog open onOpenChange={vi.fn()} sourceId={1} />
      </Wrapper>,
    );

    expect(screen.getByLabelText(/link type/i)).toHaveValue('None');
    expect(screen.getByLabelText(/context/i)).toHaveValue('');
  });

  it(
    'LOAD-BEARING: default confirm (None, no context) sends no query string on the POST — ' +
      'reverting the buildQueryString omission in useLinkNodes would append linkType=None to the URL',
    async () => {
      const user = userEvent.setup();
      const onOpenChange = vi.fn();
      render(
        <Wrapper>
          <LinkNodeDialog open onOpenChange={onOpenChange} sourceId={1} />
        </Wrapper>,
      );

      await selectFirstResult(user);
      await user.click(screen.getByRole('button', { name: /^link$/i }));

      await waitFor(() => expect(onOpenChange).toHaveBeenCalledWith(false));
      expect(lastLinkUrl?.search).toBe('');
      expect(lastLinkBody).toBe(10);
    },
  );

  it(
    'LOAD-BEARING: a chosen link type + context are sent as query params, POST body stays the bare target id — ' +
      'reverting the LinkNodeDialog wiring (dropping linkType/context from mutateAsync) leaves the URL query-string-free',
    async () => {
      const user = userEvent.setup();
      const onOpenChange = vi.fn();
      render(
        <Wrapper>
          <LinkNodeDialog open onOpenChange={onOpenChange} sourceId={1} />
        </Wrapper>,
      );

      await selectFirstResult(user);
      await user.selectOptions(screen.getByLabelText(/link type/i), 'Unidirectional');
      await user.type(screen.getByLabelText(/context/i), 'subtask');
      await user.click(screen.getByRole('button', { name: /^link$/i }));

      await waitFor(() => expect(onOpenChange).toHaveBeenCalledWith(false));
      expect(lastLinkUrl?.searchParams.get('linkType')).toBe('Unidirectional');
      expect(lastLinkUrl?.searchParams.get('context')).toBe('subtask');
      expect(lastLinkBody).toBe(10);
    },
  );

  it('keeps the dialog open and shows an error toast on server error', async () => {
    server.use(
      http.post(`${BASE_URL}/nodes/:id/links`, () =>
        HttpResponse.json({ code: 'servererror', text: 'Internal error' }, { status: 500 }),
      ),
    );

    const { toast } = await import('sonner');
    const user = userEvent.setup();
    const onOpenChange = vi.fn();
    render(
      <Wrapper>
        <LinkNodeDialog open onOpenChange={onOpenChange} sourceId={1} />
      </Wrapper>,
    );

    await selectFirstResult(user);
    await user.click(screen.getByRole('button', { name: /^link$/i }));

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    expect(onOpenChange).not.toHaveBeenCalledWith(false);
  });
});
