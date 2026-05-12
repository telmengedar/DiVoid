/**
 * Route tree for the DiVoid SPA.
 *
 * PR 1 routes: /, /callback, /logout
 * PR 2 routes: /search, /nodes/:id (stubs only here)
 * PR 4 routes: /workspace (stub)
 * PR 5 routes: /tasks, /tasks/:projectId (stub)
 *
 * Auth-gated routes are wrapped in ProtectedRoute.
 * Public routes (/callback, /logout) are never gated.
 */

import { lazy, Suspense } from 'react';
import { Routes, Route } from 'react-router-dom';
import { ProtectedRoute } from '@/features/auth/ProtectedRoute';
import { Callback } from '@/features/auth/Callback';
import { LogoutLanding } from '@/features/auth/LogoutLanding';
import { LandingPage } from '@/features/auth/LandingPage';
import { AppShell } from '@/components/layout';
import { ROUTES } from '@/lib/constants';

// Lazy-loaded placeholder routes — real implementation in later PRs.
const WorkspacePage = lazy(() =>
  import('@/features/workspace/WorkspacePage').then((m) => ({ default: m.WorkspacePage })),
);
const TasksPage = lazy(() =>
  import('@/features/tasks/TasksPage').then((m) => ({ default: m.TasksPage })),
);

function RouteLoadingFallback() {
  return (
    <div className="flex h-full items-center justify-center">
      <div className="text-muted-foreground text-sm">Loading…</div>
    </div>
  );
}

export function AppRoutes() {
  return (
    <Suspense fallback={<RouteLoadingFallback />}>
      <Routes>
        {/* ── Public routes (no auth required) ── */}
        <Route path={ROUTES.CALLBACK} element={<Callback />} />
        <Route path={ROUTES.LOGOUT} element={<LogoutLanding />} />

        {/* ── Auth-gated routes (wrapped in AppShell) ── */}
        <Route
          path={ROUTES.HOME}
          element={
            <ProtectedRoute>
              <AppShell>
                <LandingPage />
              </AppShell>
            </ProtectedRoute>
          }
        />
        <Route
          path={ROUTES.WORKSPACE}
          element={
            <ProtectedRoute>
              <AppShell>
                <WorkspacePage />
              </AppShell>
            </ProtectedRoute>
          }
        />
        <Route
          path={ROUTES.TASKS}
          element={
            <ProtectedRoute>
              <AppShell>
                <TasksPage />
              </AppShell>
            </ProtectedRoute>
          }
        />
        <Route
          path={`${ROUTES.TASKS}/:projectId`}
          element={
            <ProtectedRoute>
              <AppShell>
                <TasksPage />
              </AppShell>
            </ProtectedRoute>
          }
        />
      </Routes>
    </Suspense>
  );
}
