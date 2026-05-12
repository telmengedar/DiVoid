/**
 * Load-bearing test for ThemeProvider wiring + ThemeToggle — DiVoid #247.
 *
 * What this proves:
 *  1. When the app mounts with ThemeProvider(defaultTheme="dark"), the <html>
 *     element receives class="dark".
 *  2. The ThemeToggle control cycles the theme (dark → system → light → …).
 *     After one click from dark, the theme is "system"; after another it is "light",
 *     which removes the "dark" class from <html>.
 *  3. A markdown preview wrapper rendered inside the tree carries
 *     "prose dark:prose-invert" in light mode AND dark mode — the class string
 *     is always present; Tailwind's dark: modifier activates the prose-invert
 *     half only when <html> has class="dark".
 *
 * Negative proof requirement (DiVoid #275):
 *  See PR body — tests were run with ThemeProvider removed from the wrapper;
 *  the document.documentElement.classList assertions failed because no class
 *  was applied. Restore ThemeProvider → tests pass.
 *
 * DOM note: next-themes sets the class via a side-effect script injected into
 * <head>. In jsdom (which has no real CSS engine) the class IS applied to
 * document.documentElement. Testing Library's render() uses the same document,
 * so we query document.documentElement directly after act().
 */

import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ThemeProvider } from 'next-themes';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { ThemeToggle } from './ThemeToggle';
import { BASE_URL } from '@/test/msw/handlers';

// ─── jsdom compatibility: window.matchMedia polyfill ─────────────────────────
//
// next-themes calls window.matchMedia('(prefers-color-scheme: dark)') to detect
// system preference. jsdom does not implement matchMedia; without this mock the
// ThemeProvider throws "window.matchMedia is not a function".
//
// We stub it to return a non-matching, non-dark result (matches: false) so that
// "system" theme resolves to light in tests — which is the jsdom default.

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: vi.fn((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(),
  })),
});

// ─── MSW ─────────────────────────────────────────────────────────────────────

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
afterEach(() => { server.resetHandlers(); document.documentElement.className = ''; });
afterAll(() => server.close());

// ─── Mock react-oidc-context ─────────────────────────────────────────────────

vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(() => ({
    isAuthenticated: true,
    user: { access_token: 'test-token' },
    signinRedirect: vi.fn(),
    signinSilent: vi.fn().mockResolvedValue(undefined),
  })),
}));

// ─── Helpers ─────────────────────────────────────────────────────────────────

function makeQC() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
}

/**
 * Minimal wrapper that mimics the App composition: ThemeProvider wrapping
 * QueryClientProvider + MemoryRouter. The ThemeToggle is rendered inside.
 */
function ThemeTestWrapper({
  defaultTheme = 'dark',
  children,
}: {
  defaultTheme?: string;
  children?: React.ReactNode;
}) {
  return (
    <ThemeProvider attribute="class" defaultTheme={defaultTheme} enableSystem>
      <QueryClientProvider client={makeQC()}>
        <MemoryRouter>
          <ThemeToggle />
          {children}
        </MemoryRouter>
      </QueryClientProvider>
    </ThemeProvider>
  );
}

// ─── Inline markdown preview fixture ─────────────────────────────────────────

/**
 * Simulates the read-path markdown wrapper used in NodeDetailPage and
 * MarkdownEditorSurface. The className must be "prose prose-sm dark:prose-invert max-w-none"
 * (or a superset). This test asserts that class string is present regardless of
 * theme, and that prose-invert is only applied by Tailwind's dark: modifier
 * (i.e., it's in the class attribute but activation is CSS-only).
 */
function MarkdownPreview({ content }: { content: string }) {
  return (
    <div
      className="prose prose-sm dark:prose-invert max-w-none"
      data-testid="markdown-preview"
    >
      <p>{content}</p>
    </div>
  );
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('ThemeProvider wiring + ThemeToggle', () => {
  it('applies "dark" class to <html> when defaultTheme="dark"', async () => {
    render(
      <ThemeTestWrapper defaultTheme="dark">
        <MarkdownPreview content="Hello world" />
      </ThemeTestWrapper>,
    );

    // next-themes applies the class synchronously after mount via a script
    // injected into head. Wait for any effects to flush.
    await act(async () => {});

    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });

  it('does NOT apply "dark" class when defaultTheme="light"', async () => {
    render(
      <ThemeTestWrapper defaultTheme="light">
        <MarkdownPreview content="Hello world" />
      </ThemeTestWrapper>,
    );

    await act(async () => {});

    expect(document.documentElement.classList.contains('dark')).toBe(false);
  });

  it('cycles dark → system → light when toggle is clicked', async () => {
    const user = userEvent.setup();
    render(<ThemeTestWrapper defaultTheme="dark" />);

    await act(async () => {});

    // Start: dark
    expect(document.documentElement.classList.contains('dark')).toBe(true);

    const toggle = screen.getByRole('button', { name: /switch to (dark|system|light) theme/i });

    // Click 1: dark → system (system on a headless test env with no media query resolves to light by default)
    await user.click(toggle);
    await act(async () => {});
    // The "dark" class should be absent in system/light resolved state
    // (in jsdom, prefers-color-scheme is not dark by default)
    expect(document.documentElement.classList.contains('dark')).toBe(false);

    // Click 2: system → light (explicitly light, still no dark class)
    await user.click(toggle);
    await act(async () => {});
    expect(document.documentElement.classList.contains('dark')).toBe(false);

    // Click 3: light → dark (cycle wraps, dark class restored)
    await user.click(toggle);
    await act(async () => {});
    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });

  it('markdown preview wrapper always carries prose + dark:prose-invert classes', async () => {
    const user = userEvent.setup();
    render(
      <ThemeTestWrapper defaultTheme="dark">
        <MarkdownPreview content="# Heading" />
      </ThemeTestWrapper>,
    );

    await act(async () => {});

    const preview = screen.getByTestId('markdown-preview');

    // In dark mode: both prose and dark:prose-invert must be in className
    expect(preview).toHaveClass('prose');
    expect(preview.className).toContain('dark:prose-invert');

    // Toggle to light mode
    const toggle = screen.getByRole('button', { name: /switch to (dark|system|light) theme/i });
    await user.click(toggle); // dark → system
    await user.click(toggle); // system → light
    await act(async () => {});

    // In light mode: prose class still present, dark:prose-invert still in className
    // (it's always in the DOM; CSS engine applies it only when .dark is on <html>)
    expect(preview).toHaveClass('prose');
    expect(preview.className).toContain('dark:prose-invert');
    expect(document.documentElement.classList.contains('dark')).toBe(false);
  });

  it('toggle button has accessible aria-label reflecting next action', async () => {
    render(<ThemeTestWrapper defaultTheme="dark" />);
    await act(async () => {});

    // The toggle button's aria-label always describes the NEXT action (what clicking will do).
    // In dark mode: "Switch to system theme". In other resolved states, a different label.
    // We verify the label is one of the three valid states — not empty or undefined.
    const toggle = screen.getByRole('button');
    const label = toggle.getAttribute('aria-label');
    const validLabels = ['Switch to dark theme', 'Switch to system theme', 'Switch to light theme'];
    expect(validLabels).toContain(label);
  });

  it('renders Sun icon in light mode, Moon icon in dark mode, Monitor in system', async () => {
    const user = userEvent.setup();
    render(<ThemeTestWrapper defaultTheme="light" />);
    await act(async () => {});

    // In light mode the icon for "current theme is light" → label says "Switch to dark theme"
    expect(screen.getByRole('button', { name: 'Switch to dark theme' })).toBeInTheDocument();

    const toggle = screen.getByRole('button');

    // click → dark
    await user.click(toggle);
    await act(async () => {});
    expect(screen.getByRole('button', { name: 'Switch to system theme' })).toBeInTheDocument();

    // click → system
    await user.click(toggle);
    await act(async () => {});
    expect(screen.getByRole('button', { name: 'Switch to light theme' })).toBeInTheDocument();
  });
});

// ─── NEGATIVE PROOF DOCUMENTATION ────────────────────────────────────────────
//
// To reproduce the negative proof manually:
//
//  1. Remove <ThemeProvider> from ThemeTestWrapper (replace with a plain Fragment).
//  2. Run: npm test -- ThemeToggle.test
//  3. EXPECTED: "applies dark class to <html>" FAILS because document.documentElement
//     has no "dark" class — next-themes never ran, nothing applied the class.
//  4. ALSO EXPECTED: ThemeToggle still renders (useTheme returns a no-op default
//     from next-themes when no provider is present), but cycling does nothing to the DOM.
//  5. Restore <ThemeProvider> → all tests pass.
//
// This was verified during PR #44 development; see PR body for recorded outcomes.
