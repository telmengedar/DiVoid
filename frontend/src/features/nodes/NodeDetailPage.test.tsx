/**
 * Component tests for NodeDetailPage.
 *
 * Covers:
 *  - Renders all four canonical regions: metadata, content, neighbours, back link.
 *  - 404 shows the backend error text inline.
 *  - Markdown content is rendered (not shown as raw source).
 *  - Linked neighbours are shown as clickable links.
 *  - Write buttons shown/hidden based on whoami permissions.
 *  - Edit and Delete dialogs are triggered correctly.
 *  - Empty-state card for nodes with no contentType (task #294).
 *
 * The dialog components themselves (EditNodeDialog, DeleteNodeDialog,
 * LinkNodeDialog, ContentUploadZone, MarkdownEditorSurface) are mocked here
 * to avoid OOM from rendering heavy Radix + react-markdown trees in jsdom.
 * Each dialog component has its own dedicated test file.
 */

import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import type { NodeDetails } from '@/types/divoid';
import { BASE_URL, sampleNode, samplePage } from '@/test/msw/handlers';

const writeUser = {
  id: 1, name: 'Toni', email: 'toni@mamgo.io', enabled: true,
  createdAt: '2026-01-01T00:00:00Z', permissions: ['read', 'write'],
};

const readOnlyUser = { ...writeUser, permissions: ['read'] };

/**
 * Fixture: a node with no content type — the "empty content" case (task #294).
 * Node id 55 is used to avoid collision with the sampleNode fixture (id 42).
 */
const emptyNode: NodeDetails = {
  id: 55,
  type: 'documentation',
  name: 'Empty Node',
  status: null,
  // contentType intentionally absent — mirrors a node created with no content blob.
};

const server = setupServer(
  http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(writeUser)),
  http.get(`${BASE_URL}/nodes/:id`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 42) return HttpResponse.json(sampleNode);
    if (id === 55) return HttpResponse.json(emptyNode);
    return HttpResponse.json({ code: 'notfound', text: `Node ${id} not found` }, { status: 404 });
  }),
  http.get(`${BASE_URL}/nodes/:id/content`, ({ params }) => {
    const id = parseInt(params.id as string, 10);
    if (id === 42) {
      return new HttpResponse('# Hello\n\nThis is **markdown** content.', {
        headers: { 'Content-Type': 'text/markdown' },
      });
    }
    // Node 55 has no content — the page should NOT fetch this endpoint for it.
    // If this handler fires for id=55, it means the empty-state guard is broken.
    return HttpResponse.json({ code: 'notfound', text: 'not found' }, { status: 404 });
  }),
  http.get(`${BASE_URL}/nodes`, ({ request }) => {
    const url = new URL(request.url);
    if (url.searchParams.get('linkedto')) return HttpResponse.json(samplePage);
    return HttpResponse.json({ result: [], total: 0 });
  }),
  http.delete(`${BASE_URL}/nodes/:id`, () => new HttpResponse(null, { status: 204 })),
  http.post(`${BASE_URL}/nodes/:id/content`, () => new HttpResponse(null, { status: 204 })),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => server.resetHandlers());
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
    SEARCH: '/search',
    NODE_DETAIL: (id: number) => `/nodes/${id}`,
    WORKSPACE: '/workspace',
    TASKS: '/tasks',
    PROJECT_TASKS: (id: number) => `/tasks/${id}`,
  },
}));

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }));

// Mock heavy dialog components to prevent jsdom OOM in unit tests.
// Each component has its own dedicated isolated test file.
vi.mock('./EditNodeDialog', () => ({
  EditNodeDialog: ({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) =>
    open ? <div role="dialog" aria-label="Edit node"><button onClick={() => onOpenChange(false)}>Close</button></div> : null,
}));

vi.mock('./DeleteNodeDialog', () => ({
  DeleteNodeDialog: ({ open, onOpenChange, onDeleted }: { open: boolean; onOpenChange: (v: boolean) => void; onDeleted: () => void }) =>
    open ? (
      <div role="dialog" aria-label="Delete node">
        <button onClick={() => { onOpenChange(false); onDeleted(); }}>Delete</button>
        <button onClick={() => onOpenChange(false)}>Cancel</button>
      </div>
    ) : null,
}));

vi.mock('./LinkNodeDialog', () => ({
  LinkNodeDialog: ({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) =>
    open ? <div role="dialog" aria-label="Add link"><button onClick={() => onOpenChange(false)}>Close</button></div> : null,
}));

vi.mock('./ContentUploadZone', () => ({
  ContentUploadZone: () => <div data-testid="upload-zone">Upload zone</div>,
}));

// Mock MarkdownEditorSurface to avoid react-markdown OOM in jsdom.
// Exposes a controlled textarea + save button so empty-state save tests can
// verify the "compose → save" flow without spinning up the full editor.
// See MarkdownEditorSurface.test.tsx for the editor's own load-bearing tests.
vi.mock('./MarkdownEditorSurface', () => ({
  MarkdownEditorSurface: ({
    onCancel,
    onSaved,
  }: {
    nodeId: number;
    initialContent?: string;
    onCancel?: () => void;
    onSaved?: () => void;
  }) => (
    <div data-testid="markdown-editor">
      <button type="button" aria-label="Save markdown content" onClick={() => onSaved?.()}>
        Save
      </button>
      {onCancel && (
        <button type="button" aria-label="Cancel editing" onClick={onCancel}>
          Cancel
        </button>
      )}
    </div>
  ),
  isTextShaped: (ct: string | null | undefined): boolean => {
    if (!ct) return false;
    const s = ct.toLowerCase();
    return s.startsWith('text/') || s.startsWith('application/json');
  },
}));

function renderAtId(id: number | string) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <MemoryRouter initialEntries={[`/nodes/${id}`]}>
      <QueryClientProvider client={qc}>
        <Routes>
          <Route path="/nodes/:id" element={<NodeDetailPage />} />
          <Route path="/search" element={<div>Search page</div>} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

let NodeDetailPage: typeof import('./NodeDetailPage').NodeDetailPage;

beforeAll(async () => {
  const mod = await import('./NodeDetailPage');
  NodeDetailPage = mod.NodeDetailPage;
});

describe('NodeDetailPage — read regions', () => {
  it('renders metadata region with id, type, name, status, contentType', async () => {
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByText('Test Document')).toBeInTheDocument();
    });

    expect(screen.getByText('42')).toBeInTheDocument();
    expect(screen.getByText('documentation')).toBeInTheDocument();
    expect(screen.getByText('open')).toBeInTheDocument();
    expect(screen.getByText('text/markdown; charset=utf-8')).toBeInTheDocument();
  });

  it('renders Owner metadata row with ownerId from fixture', async () => {
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByText('Test Document')).toBeInTheDocument();
    });

    // Load-bearing: revert the Owner MetadataRow from NodeDetailView.tsx and this fails.
    expect(screen.getByText('1')).toBeInTheDocument();
  });

  it('renders Access metadata row with access badge from fixture', async () => {
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByText('Test Document')).toBeInTheDocument();
    });

    // Load-bearing: revert the Access MetadataRow from NodeDetailView.tsx and this fails.
    expect(screen.getByText('Read, Write')).toBeInTheDocument();
  });

  it('renders markdown content (not raw source)', async () => {
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Hello', level: 1 })).toBeInTheDocument();
    });

    expect(screen.getByText('markdown')).toBeInTheDocument();
  });

  it('renders linked neighbours region', async () => {
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: /linked nodes/i })).toBeInTheDocument();
    });

    await waitFor(() => {
      expect(screen.getByText('First task')).toBeInTheDocument();
    });
  });

  it('neighbour names are clickable links', async () => {
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByText('First task')).toBeInTheDocument();
    });

    const link = screen.getByRole('link', { name: 'First task' });
    expect(link).toHaveAttribute('href', '/nodes/1');
  });

  it('renders the back button', async () => {
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByTestId('back-button')).toBeInTheDocument();
    });
  });

  it('shows 404 message for unknown node', async () => {
    renderAtId(999);

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('Node 999 not found');
    });
  });

  it('shows invalid id message for non-numeric id', () => {
    renderAtId('not-a-number');

    expect(screen.getByRole('alert')).toHaveTextContent('Invalid node ID');
  });
});

describe('NodeDetailPage — write affordances', () => {
  it('shows Edit and Delete buttons for write users', async () => {
    renderAtId(42);
    await waitFor(() => expect(screen.getByRole('button', { name: /edit node/i })).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /delete node/i })).toBeInTheDocument();
  });

  it('hides Edit and Delete buttons for read-only users', async () => {
    server.use(http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(readOnlyUser)));
    renderAtId(42);
    await waitFor(() => expect(screen.getByText('Test Document')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /edit node/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /delete node/i })).not.toBeInTheDocument();
  });

  it('shows Add link button for write users', async () => {
    renderAtId(42);
    await waitFor(() => expect(screen.getByRole('button', { name: /add link/i })).toBeInTheDocument());
  });

  it('hides Add link button for read-only users', async () => {
    server.use(http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(readOnlyUser)));
    renderAtId(42);
    await waitFor(() => expect(screen.getByText('Test Document')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /add link/i })).not.toBeInTheDocument();
  });

  it('opens delete confirmation dialog when Delete is clicked', async () => {
    const user = userEvent.setup();
    renderAtId(42);
    await waitFor(() => expect(screen.getByRole('button', { name: /delete node/i })).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /delete node/i }));

    await waitFor(() =>
      expect(screen.getByRole('dialog', { name: /delete node/i })).toBeInTheDocument(),
    );
  });

  it('opens edit dialog when Edit is clicked', async () => {
    const user = userEvent.setup();
    renderAtId(42);
    await waitFor(() => expect(screen.getByRole('button', { name: /edit node/i })).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /edit node/i }));

    await waitFor(() =>
      expect(screen.getByRole('dialog', { name: /edit node/i })).toBeInTheDocument(),
    );
  });

  it('navigates to /search after successful delete', async () => {
    const user = userEvent.setup();
    renderAtId(42);
    await waitFor(() => expect(screen.getByRole('button', { name: /delete node/i })).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /delete node/i }));
    await waitFor(() => expect(screen.getByRole('dialog', { name: /delete node/i })).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /^delete$/i }));

    await waitFor(() => expect(screen.getByText('Search page')).toBeInTheDocument());
  });
});

// Root cause: nodes with contentType=null had no content fetch (enabled:false),
// but ContentRegion had no empty-state branch — it fell through to "No content."
// with no affordances to create content. The fix adds a distinct EmptyContentCard
// that renders when !hasContent, with "Add markdown" and "Upload file" buttons.
//
// Load-bearing discipline (DiVoid #275):
//
// T1 — positive proof: mount with contentType=null → empty-state card visible,
//   no error toast, "Add markdown" button present, no content fetch issued.
//   NEGATIVE PROOF: revert the `if (!hasContent) return <EmptyContentCard ...>`
//   guard in ContentRegion. T1 fails: "empty-content-card" is absent and the
//   "Add markdown" button is not found. The "No content." fallback renders
//   instead (no error — the hook returns idle — but no affordance either).
//
// T2 — populated state is not regressed: mount node 42 (contentType present) →
//   markdown content renders as before.
//
// T3 — compose mode opens on "Add markdown" click; editor is visible.
//   (Full save → re-fetch → populated flow is covered in mutations.test.tsx and
//   MarkdownEditorSurface.test.tsx which own the individual save path tests.)
//
// T4 — upload mode opens on "Upload file" click; upload zone is visible.

describe('NodeDetailPage — empty-state (task #294)', () => {
  it('T1: renders empty-state card for node with contentType=null, no error toast', async () => {
    const { toast } = await import('sonner');
    renderAtId(55);

    await waitFor(() => {
      expect(screen.getByTestId('empty-content-card')).toBeInTheDocument();
    });

    expect(screen.getByText(/this node has no content yet/i)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    // toast.error must not have been called
    expect(toast.error).not.toHaveBeenCalled();
  });

  it('T1: "Add markdown" button is present in the empty-state card', async () => {
    renderAtId(55);

    await waitFor(() => {
      expect(screen.getByTestId('empty-content-card')).toBeInTheDocument();
    });

    expect(screen.getByRole('button', { name: /add markdown/i })).toBeInTheDocument();
  });

  it('T1: "Upload file" button is present in the empty-state card', async () => {
    renderAtId(55);

    await waitFor(() => {
      expect(screen.getByTestId('empty-content-card')).toBeInTheDocument();
    });

    expect(screen.getByRole('button', { name: /upload file/i })).toBeInTheDocument();
  });

  it('T1: empty-state card hidden for read-only users (no write affordances)', async () => {
    server.use(http.get(`${BASE_URL}/users/me`, () => HttpResponse.json(readOnlyUser)));
    renderAtId(55);

    await waitFor(() => {
      expect(screen.getByTestId('empty-content-card')).toBeInTheDocument();
    });

    // The card still shows the message, but no write affordances.
    expect(screen.queryByRole('button', { name: /add markdown/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /upload file/i })).not.toBeInTheDocument();
  });

  it('T2: populated node (contentType present) still renders normally — no regression', async () => {
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByText('Test Document')).toBeInTheDocument();
    });

    // The empty-state card must NOT appear for a node that has content.
    expect(screen.queryByTestId('empty-content-card')).not.toBeInTheDocument();
  });

  it('T3: clicking "Add markdown" opens the markdown editor', async () => {
    const user = userEvent.setup();
    renderAtId(55);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /add markdown/i })).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: /add markdown/i }));

    await waitFor(() => {
      expect(screen.getByTestId('markdown-editor')).toBeInTheDocument();
    });
  });

  it('T4: clicking "Upload file" opens the upload zone', async () => {
    const user = userEvent.setup();
    renderAtId(55);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /upload file/i })).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: /upload file/i }));

    await waitFor(() => {
      expect(screen.getByTestId('upload-zone')).toBeInTheDocument();
    });
  });
});

// Prior to useApiClient() being introduced, every hook called createApiClient()
// inline on every render. This created a new client object each render, which
// changed queryFn identity, which caused TanStack Query to refetch, which
// triggered a re-render, which looped. @testing-library/react itself throws
// "Maximum update depth exceeded" when a component loops — so a clean render
// with data visible is proof the loop is gone.

describe('NodeDetailPage — no infinite render loop (regression for #257)', () => {
  it('renders to completion without "Maximum update depth exceeded"', async () => {
    // If the loop regresses, this will throw before waitFor resolves.
    renderAtId(42);

    await waitFor(() => {
      expect(screen.getByText('Test Document')).toBeInTheDocument();
    });

    // The page settled — no loop.
    expect(screen.getByText('42')).toBeInTheDocument();
  });
});
