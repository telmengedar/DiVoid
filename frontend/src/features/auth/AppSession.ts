import { createContext, useContext } from 'react';
import type { AuthContextProps } from 'react-oidc-context';
import type { ApiClient } from '@/lib/api';
import type { UserDetails } from '@/types/divoid';

/** Describes a verified memory-only key session and its isolated transport. */
export interface ApiKeySession {
  mode: 'api-key';
  user?: never;
  isAuthenticated: boolean;
  isLoading: boolean;
  identity?: UserDetails;
  error?: Error;
  client: ApiClient;
  login: (candidate: string) => Promise<void>;
  signinRedirect: () => void;
  signoutRedirect: () => void;
}

/** Retains native OIDC state alongside the separate API-key session protocol. */
export type AppSession = (AuthContextProps & { mode: 'oidc' }) | ApiKeySession;

/** Provides a single authenticated-state authority to every app consumer. */
export const AppSessionContext = createContext<AppSession | undefined>(undefined);

/** Returns the app session supplied by the selected authentication provider. */
export function useAuth(): AppSession {
  const session = useContext(AppSessionContext);
  if (!session) throw new Error('Authentication provider is missing');
  return session;
}
