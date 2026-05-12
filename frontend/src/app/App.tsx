/**
 * App root — composition only.
 *
 * Wraps the component tree with:
 *  1. QueryClientProvider (TanStack Query)
 *  2. BrowserRouter (react-router-dom)
 *  3. AuthProvider (react-oidc-context / Keycloak PKCE)
 *  4. AppRoutes (route tree)
 *  5. Toaster (sonner)
 *
 * No business logic here. No data fetching here.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.1
 */

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter } from 'react-router-dom';
import { AuthProvider } from '@/features/auth/AuthProvider';
import { AppRoutes } from './routes';
import { Toaster } from 'sonner';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // 5 minutes stale time — lists don't need to refetch on every mount.
      staleTime: 5 * 60 * 1_000,
      // Retry once on failure (covers transient network blips).
      retry: 1,
      // Refetch on window focus so permissions changes are visible promptly.
      refetchOnWindowFocus: true,
    },
  },
});

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AuthProvider>
          <AppRoutes />
          <Toaster position="top-right" richColors />
        </AuthProvider>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
