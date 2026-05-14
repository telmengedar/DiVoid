/**
 * Route tree for the DiVoid SPA.
 *
 * PR 1 routes: /, /callback, /logout
 * PR 2 routes: /search, /nodes/:id
 * PR 4 routes: /workspace (stub)
 * PR 5 step 1 routes: /tasks, /tasks/orgs/:orgId, /tasks/projects/:projectId
 *
 * Auth-gated routes are wrapped in ProtectedRoute.
 * Public routes (/callback, /logout) are never gated.
 *
 * LocationTracker: a side-effect-only component that writes the previous in-app
 * location to sessionStorage('divoid.lastLocation') on every navigation.
 * NodeDetailPage reads this key for its back button (bug #388).
 */

import { lazy, Suspense } from 'react';
import { Routes, Route, Navigate } from 'react-router-dom';
import { LocationTracker } from './LocationTracker';
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

// PR 2 routes — search + node detail.
const SearchPage = lazy(() =>
  import('@/features/nodes/SearchPage').then((m) => ({ default: m.SearchPage })),
);
const NodeDetailPage = lazy(() =>
  import('@/features/nodes/NodeDetailPage').then((m) => ({ default: m.NodeDetailPage })),
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
      <LocationTracker />
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

        {/* PR 2 — search and node detail */}
        <Route
          path={ROUTES.SEARCH}
          element={
            <ProtectedRoute>
              <AppShell>
                <SearchPage />
              </AppShell>
            </ProtectedRoute>
          }
        />
        <Route
          path="/nodes/:id"
          element={
            <ProtectedRoute>
              <AppShell>
                <NodeDetailPage />
              </AppShell>
            </ProtectedRoute>
          }
        />

        {/* PR 4 — workspace (stub) */}
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

        {/* PR 5 — inline org + project pill rows (DiVoid task #391) */}
        {/* /tasks — empty-state landing with pill rows */}
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
        {/* /tasks/orgs/:orgId — legacy drill-down URL; redirect to /tasks */}
        <Route
          path="/tasks/orgs/:orgId"
          element={<Navigate to="/tasks" replace />}
        />
        {/* /tasks/projects/:projectId — canonical task list URL */}
        <Route
          path="/tasks/projects/:projectId"
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
