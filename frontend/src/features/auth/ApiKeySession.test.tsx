import { StrictMode, useState } from 'react';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { AuthProvider } from './AuthProvider';
import { ProtectedRoute } from './ProtectedRoute';
import { useAuth } from './AppSession';
import { useWhoami } from './useWhoami';
import { useNode } from '@/features/nodes/useNode';
import { useApiClient } from '@/lib/useApiClient';
import type { UserDetails } from '@/types/divoid';

vi.mock('./authMode', () => ({ AUTH_MODE: 'api-key' }));
vi.mock('react-oidc-context', () => ({
  AuthProvider: () => { throw new Error('API-key mode initialized OIDC'); },
  useAuth: () => { throw new Error('API-key mode requested OIDC'); },
}));
vi.mock('@/lib/constants', async (original) => ({
  ...await original<typeof import('@/lib/constants')>(),
  API_BASE_URL: 'http://localhost/api',
}));

const base = 'http://localhost/api';
const first: UserDetails = {
  id: 4, name: 'First reader', email: '', enabled: true,
  createdAt: '2026-01-01T00:00:00Z', permissions: ['read'], homeNodeId: null,
};
const second: UserDetails = { ...first, id: 5, name: 'Second reader' };
let identityRequests: string[] = [];
let graphRequests: string[] = [];
const server = setupServer(
  http.get(`${base}/users/me`, ({ request }) => {
    const bearer = request.headers.get('Authorization') ?? '';
    identityRequests.push(bearer);
    return HttpResponse.json(bearer === 'Bearer second-test-key' ? second : first);
  }),
  http.get(`${base}/nodes/42`, ({ request }) => {
    const bearer = request.headers.get('Authorization') ?? '';
    graphRequests.push(bearer);
    return HttpResponse.json({ id: 42, name: bearer === 'Bearer second-test-key' ? 'Second graph' : 'First graph', type: 'project', status: null });
  }),
  http.get(`${base}/denied`, () => HttpResponse.json({ code: 'forbidden', text: 'Denied' }, { status: 403 })),
  http.get(`${base}/expired`, () => HttpResponse.json({ code: 'unauthorized', text: 'Expired' }, { status: 401 })),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterAll(() => server.close());
beforeEach(() => {
  identityRequests = [];
  graphRequests = [];
  localStorage.clear();
  sessionStorage.clear();
});
afterEach(() => {
  cleanup();
  server.resetHandlers();
});

function Probe() {
  const auth = useAuth();
  const client = useApiClient();
  const identity = useWhoami();
  const node = useNode(42);
  const queries = useQueryClient();
  const [message, setMessage] = useState('');
  const pending = useMutation({
    mutationFn: () => client.get<string>('/late'),
    onSuccess: (value) => queries.setQueryData(['late'], value),
  });
  return <>
    <p>{identity.data?.name}</p>
    <p>{node.data?.name}</p>
    <p role="status">{message}</p>
    <button onClick={() => auth.signoutRedirect()}>Sign out</button>
    <button onClick={() => void client.get('/denied').catch(() => setMessage('Access denied'))}>Denied</button>
    <button onClick={() => void client.get('/expired').catch(() => {})}>Expire</button>
    <button onClick={() => pending.mutate()}>Start delayed request</button>
    <button onClick={() => setMessage(String(queries.getQueryData(['late']) ?? 'No late data'))}>Inspect cache</button>
  </>;
}

function Location() {
  const location = useLocation();
  return <output aria-label="Current location">{location.pathname + location.search + location.hash}</output>;
}

function mount(path = '/nodes/42?view=graph#details') {
  return render(
    <StrictMode>
      <MemoryRouter initialEntries={[path]}>
        <AuthProvider>
          <Location />
          <Routes><Route path="*" element={<ProtectedRoute><Probe /></ProtectedRoute>} /></Routes>
        </AuthProvider>
      </MemoryRouter>
    </StrictMode>,
  );
}

async function signIn(value = 'first-test-key') {
  fireEvent.change(screen.getByLabelText('API key'), { target: { value } });
  fireEvent.submit(screen.getByRole('form', { name: 'Sign in to DiVoid' }));
  await screen.findByText(value.trim() === 'second-test-key' ? 'Second graph' : 'First graph');
}

describe('API-key application session', () => {
  it('validates identity before admitting a real protected node query and resumes the untouched deep link', async () => {
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => { release = resolve; });
    server.use(http.get(`${base}/users/me`, async ({ request }) => {
      identityRequests.push(request.headers.get('Authorization') ?? '');
      await gate;
      return HttpResponse.json(first);
    }));
    mount();
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: '  first-test-key  ' } });
    fireEvent.submit(screen.getByRole('form', { name: 'Sign in to DiVoid' }));
    await waitFor(() => expect(identityRequests).toEqual(['Bearer first-test-key']));
    expect(graphRequests).toEqual([]);
    expect(screen.getByLabelText('API key')).toHaveValue('');
    await act(async () => release());
    await screen.findByText('First graph');
    expect(screen.getByText('First reader')).toBeInTheDocument();
    expect(graphRequests.length).toBeGreaterThan(0);
    expect(graphRequests.every((bearer) => bearer === 'Bearer first-test-key')).toBe(true);
    expect(screen.getByLabelText('Current location')).toHaveTextContent('/nodes/42?view=graph#details');
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });

  it.each([401, 403, 'network'] as const)('rejects %s safely without admitting graph access or reflecting server text', async (failure) => {
    server.use(http.get(`${base}/users/me`, () => failure === 'network'
      ? HttpResponse.error()
      : HttpResponse.json({ code: 'unauthorized', text: 'sensitive-test-response' }, { status: failure })));
    mount();
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'rejected-test-key' } });
    fireEvent.submit(screen.getByRole('form', { name: 'Sign in to DiVoid' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(failure === 'network' ? 'Unable to reach' : 'not accepted');
    expect(screen.getByLabelText('API key')).toHaveValue('');
    expect(graphRequests).toEqual([]);
    expect(document.body).not.toHaveTextContent('sensitive-test-response');
    expect(document.body).not.toHaveTextContent('rejected-test-key');
  });

  it('rejects an empty candidate locally and allows retry with a valid key', async () => {
    mount();
    fireEvent.submit(screen.getByRole('form', { name: 'Sign in to DiVoid' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter your API key');
    expect(identityRequests).toEqual([]);
    await signIn();
    expect(screen.getByText('First reader')).toBeInTheDocument();
  });

  it('keeps a 403 session but clears a 401 session and permits clean reentry without OIDC', async () => {
    mount();
    await signIn();
    fireEvent.click(screen.getByText('Denied'));
    await screen.findByText('Access denied');
    expect(screen.getByText('First reader')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Expire'));
    await screen.findByLabelText('API key');
    expect(screen.queryByText('First graph')).not.toBeInTheDocument();
    await signIn('second-test-key');
    expect(screen.getByText('Second reader')).toBeInTheDocument();
    expect(screen.queryByText('First reader')).not.toBeInTheDocument();
  });

  it('discards pending responses and the old identity cache when logging out and switching users', async () => {
    let started = false;
    let aborted = false;
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => { release = resolve; });
    server.use(http.get(`${base}/late`, async ({ request }) => {
      started = true;
      request.signal.addEventListener('abort', () => { aborted = true; });
      await gate;
      return HttpResponse.json('Old private result');
    }));
    mount();
    await signIn();
    fireEvent.click(screen.getByText('Start delayed request'));
    await waitFor(() => expect(started).toBe(true));
    fireEvent.click(screen.getByText('Sign out'));
    await screen.findByLabelText('API key');
    await waitFor(() => expect(aborted).toBe(true));
    await signIn('second-test-key');
    await act(async () => release());
    fireEvent.click(screen.getByText('Inspect cache'));
    expect(screen.getByText('No late data')).toBeInTheDocument();
    expect(screen.queryByText('First reader')).not.toBeInTheDocument();
    expect(screen.queryByText('First graph')).not.toBeInTheDocument();
  });

  it('loses the key on remount and requires reentry at the same relative deep link without following external return parameters', async () => {
    const path = '/nodes/42?returnTo=https://untrusted.invalid';
    const view = mount(path);
    await signIn();
    view.unmount();
    mount(path);
    expect(screen.getByLabelText('API key')).toHaveValue('');
    expect(screen.queryByText('First reader')).not.toBeInTheDocument();
    await signIn();
    expect(screen.getByLabelText('Current location')).toHaveTextContent(path);
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });
});
