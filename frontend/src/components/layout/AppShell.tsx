/**
 * App shell — the outer layout wrapping all authenticated routes.
 *
 * Renders the TopNav + a scrollable main content area.
 * Pure composition: no data fetching, no business logic.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.1
 */

import { type ReactNode } from 'react';
import { TopNav } from './TopNav';

interface AppShellProps {
  children: ReactNode;
}

export function AppShell({ children }: AppShellProps) {
  return (
    <div className="flex flex-col h-screen overflow-hidden">
      <TopNav />
      <main className="flex-1 overflow-auto" id="main-content">
        {children}
      </main>
    </div>
  );
}
