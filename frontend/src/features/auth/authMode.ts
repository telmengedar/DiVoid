const configuredMode = import.meta.env.VITE_AUTH_MODE ?? 'oidc';

if (configuredMode !== 'oidc' && configuredMode !== 'api-key') {
  throw new Error('Unsupported authentication mode');
}

/** Selects the explicitly configured browser authentication protocol. */
export const AUTH_MODE = configuredMode;
