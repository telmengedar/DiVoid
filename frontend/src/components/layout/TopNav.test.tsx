import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { ApiKeyProvider } from '@/features/auth/ApiKeyProvider';
import { ProtectedRoute } from '@/features/auth/ProtectedRoute';
import { API_BASE_URL } from '@/lib/constants';
import { TopNav } from './TopNav';

const server = setupServer(
  http.get(`${API_BASE_URL}/users/me`, () => HttpResponse.json({
    id: 4, name: 'Menu reader', email: '', enabled: true,
    createdAt: '2026-01-01T00:00:00Z', permissions: ['read'], homeNodeId: null,
  })),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => { cleanup(); server.resetHandlers(); });
afterAll(() => server.close());

describe('TopNav user menu', () => {
  it.each(['pointer', 'keyboard'] as const)('opens the real dropdown with %s and signs out of the protected session', async (interaction) => {
    const user = userEvent.setup();
    render(
      <MemoryRouter initialEntries={['/workspace']}>
        <ApiKeyProvider>
          <ProtectedRoute>
            <TopNav />
            <p>Protected workspace</p>
          </ProtectedRoute>
        </ApiKeyProvider>
      </MemoryRouter>,
    );

    await user.type(screen.getByLabelText('API key'), 'menu-test-key');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByText('Protected workspace')).toBeInTheDocument();
    const trigger = screen.getByRole('button', { name: 'User menu for Menu reader' });
    expect(trigger).toHaveAttribute('aria-haspopup', 'menu');
    expect(trigger).toHaveAttribute('aria-expanded', 'false');

    if (interaction === 'pointer') {
      await user.click(trigger);
    } else {
      trigger.focus();
      await user.keyboard('{Enter}');
    }

    const signOut = await screen.findByRole('menuitem', { name: 'Sign out' });
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    if (interaction === 'pointer') {
      await user.click(signOut);
    } else {
      expect(signOut).toHaveFocus();
      await user.keyboard('{Enter}');
    }

    expect(await screen.findByLabelText('API key')).toHaveValue('');
    expect(screen.queryByText('Protected workspace')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'User menu for Menu reader' })).not.toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Sign out' })).not.toBeInTheDocument();
  });
});
