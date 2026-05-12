/**
 * Tests for MarkdownEditorSurface.
 *
 * Covers:
 *  - Pre-populates textarea with initialContent when provided.
 *  - Preview tab renders the in-progress markdown (same react-markdown renderer).
 *  - Save button dispatches POST /api/nodes/{id}/content with text/markdown body.
 *  - No render-loop reintroduced (harness in src/test/setup.ts catches it).
 *
 * Load-bearing proof (DiVoid #275):
 *  The "save dispatches POST" test is the load-bearing pin for the save path.
 *  See PR #43 comment for negative + positive proof outcomes.
 *
 * Task: DiVoid node #276
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BASE_URL } from '@/test/msw/handlers';

// ─── MSW server ───────────────────────────────────────────────────────────────

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () =>
    HttpResponse.json({
      id: 1, name: 'Toni', email: 'toni@mamgo.io',
      enabled: true, createdAt: '2026-01-01T00:00:00Z',
      permissions: ['read', 'write'],
    }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

// ─── Mocks ────────────────────────────────────────────────────────────────────

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
    SEARCH: '/search',
    NODE_DETAIL: (id: number) => `/nodes/${id}`,
    WORKSPACE: '/workspace',
    TASKS: '/tasks',
    PROJECT_TASKS: (id: number) => `/tasks/${id}`,
  },
}));

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }));

// ─── Import lazily so mocks are registered first ──────────────────────────────

let MarkdownEditorSurface: typeof import('./MarkdownEditorSurface').MarkdownEditorSurface;
let isTextShaped: typeof import('./MarkdownEditorSurface').isTextShaped;

beforeAll(async () => {
  const mod = await import('./MarkdownEditorSurface');
  MarkdownEditorSurface = mod.MarkdownEditorSurface;
  isTextShaped = mod.isTextShaped;
});

// ─── Helpers ──────────────────────────────────────────────────────────────────

function renderEditor(nodeId = 42, initialContent = '') {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={qc}>
      <MarkdownEditorSurface nodeId={nodeId} initialContent={initialContent} />
    </QueryClientProvider>,
  );
}

// ─── isTextShaped unit tests ──────────────────────────────────────────────────

describe('isTextShaped', () => {
  it('returns true for text/markdown', () => {
    expect(isTextShaped('text/markdown; charset=utf-8')).toBe(true);
  });

  it('returns true for text/plain', () => {
    expect(isTextShaped('text/plain')).toBe(true);
  });

  it('returns true for application/json', () => {
    expect(isTextShaped('application/json')).toBe(true);
  });

  it('returns false for image/png', () => {
    expect(isTextShaped('image/png')).toBe(false);
  });

  it('returns false for application/octet-stream', () => {
    expect(isTextShaped('application/octet-stream')).toBe(false);
  });

  it('returns false for null', () => {
    expect(isTextShaped(null)).toBe(false);
  });

  it('returns false for empty string', () => {
    expect(isTextShaped('')).toBe(false);
  });
});

// ─── Editor mount and pre-population ─────────────────────────────────────────

describe('MarkdownEditorSurface — pre-population', () => {
  it('renders the Write and Preview tabs', () => {
    renderEditor(42, '');
    expect(screen.getByRole('tab', { name: /write/i })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /preview/i })).toBeInTheDocument();
  });

  it('pre-populates the textarea with initialContent', () => {
    renderEditor(42, '# Existing heading\n\nSome text.');
    const textarea = screen.getByRole('textbox', { name: /markdown content editor/i });
    expect(textarea).toHaveValue('# Existing heading\n\nSome text.');
  });

  it('renders empty textarea when no initialContent', () => {
    renderEditor(42, '');
    const textarea = screen.getByRole('textbox', { name: /markdown content editor/i });
    expect(textarea).toHaveValue('');
  });

  it('does not reintroduce a render loop on mount', async () => {
    // The render-stability harness in src/test/setup.ts catches
    // "Maximum update depth exceeded" in console.error and throws in afterEach.
    // A clean render with the component visible proves no loop.
    renderEditor(42, '# Hello');
    const textarea = screen.getByRole('textbox', { name: /markdown content editor/i });
    expect(textarea).toBeInTheDocument();
  });
});

// ─── Preview tab ─────────────────────────────────────────────────────────────

describe('MarkdownEditorSurface — preview tab', () => {
  it('shows "Nothing to preview" when draft is empty', async () => {
    const user = userEvent.setup();
    renderEditor(42, '');
    await user.click(screen.getByRole('tab', { name: /preview/i }));
    expect(screen.getByText(/nothing to preview/i)).toBeInTheDocument();
  });

  it('renders markdown via react-markdown in the preview panel', async () => {
    const user = userEvent.setup();
    renderEditor(42, '# My Heading\n\nSome **bold** text.');
    await user.click(screen.getByRole('tab', { name: /preview/i }));

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'My Heading', level: 1 })).toBeInTheDocument();
    });

    expect(screen.getByText('bold')).toBeInTheDocument();
  });

  it('preview reflects edits made before switching tabs', async () => {
    const user = userEvent.setup();
    renderEditor(42, '## Initial');

    const textarea = screen.getByRole('textbox', { name: /markdown content editor/i });
    await user.clear(textarea);
    await user.type(textarea, '## Updated heading');

    await user.click(screen.getByRole('tab', { name: /preview/i }));

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Updated heading', level: 2 })).toBeInTheDocument();
    });
  });
});

// ─── Save path (load-bearing) ─────────────────────────────────────────────────
//
// This is the load-bearing test for the save-on-click logic (DiVoid #275).
//
// The real footgun shape being guarded against:
//   useEffect(() => { mutation.mutate(draft); }, [draft, mutation]);
//   — this fires on every draft change and on every render that changes
//     mutation identity (TanStack Query), causing an infinite loop.
//
// The correct shape (asserted here): mutation.mutate is called only when
// the user explicitly clicks "Save". We verify by:
//   1. Capturing the POST request body.
//   2. Asserting it only arrives after the button click, not before.

describe('MarkdownEditorSurface — save (load-bearing)', () => {
  it('POST /api/nodes/{id}/content is sent on Save click with text/markdown body', async () => {
    const user = userEvent.setup();

    let capturedBody: string | null = null;
    let capturedContentType: string | null = null;

    server.use(
      http.post(`${BASE_URL}/nodes/42/content`, async ({ request }) => {
        capturedContentType = request.headers.get('content-type');
        capturedBody = await request.text();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderEditor(42, '# Hello');

    // Verify no POST has been sent yet (before save click).
    expect(capturedBody).toBeNull();

    // Click save.
    const saveBtn = screen.getByRole('button', { name: /save markdown content/i });
    await user.click(saveBtn);

    await waitFor(() => {
      expect(capturedBody).not.toBeNull();
    });

    // Verify the body is the draft content.
    expect(capturedBody).toBe('# Hello');
    // Verify the Content-Type is text/markdown; charset=utf-8.
    expect(capturedContentType).toMatch(/text\/markdown/);
    expect(capturedContentType).toMatch(/charset=utf-8/);
  });

  it('does not POST before the Save button is clicked', async () => {
    let postCount = 0;

    server.use(
      http.post(`${BASE_URL}/nodes/42/content`, () => {
        postCount++;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const user = userEvent.setup();
    renderEditor(42, '# Initial');

    const textarea = screen.getByRole('textbox', { name: /markdown content editor/i });

    // Type into the editor — if there were a buggy useEffect on draft,
    // each keystroke would trigger a POST.
    await user.type(textarea, ' appended text');

    // Still zero POSTs — the mutation should not have fired.
    expect(postCount).toBe(0);
  });

  it('Save button is disabled while mutation is pending', async () => {
    let resolvePost: () => void;
    const postStarted = new Promise<void>((res) => { resolvePost = res; });

    server.use(
      http.post(`${BASE_URL}/nodes/42/content`, async () => {
        resolvePost!();
        // Never resolve — keeps the mutation in "pending" state.
        await new Promise(() => {});
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const user = userEvent.setup();
    renderEditor(42, '# Hello');

    const saveBtn = screen.getByRole('button', { name: /save markdown content/i });
    await user.click(saveBtn);

    await postStarted;

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /save markdown content/i })).toBeDisabled();
    });
  });
});
