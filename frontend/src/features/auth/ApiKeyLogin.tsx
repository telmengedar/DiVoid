import { useState, type FormEvent } from 'react';
import type { ApiKeySession } from './AppSession';

/** Collects a transient key without changing the protected deep-link location.
 * @param session Authentication state and the identity-validation action.
 */
export function ApiKeyLogin({ session }: { session: ApiKeySession }) {
  const [candidate, setCandidate] = useState('');

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const value = candidate;
    setCandidate('');
    await session.login(value);
  }

  return (
    <main className="min-h-screen flex items-center justify-center bg-background text-foreground p-4">
      <form onSubmit={submit} className="w-full max-w-sm space-y-4" aria-label="Sign in to DiVoid">
        <h1 className="text-xl font-medium">Sign in to DiVoid</h1>
        <p className="text-sm text-muted-foreground">Enter your API key. Signing out or reloading this page clears it.</p>
        <label htmlFor="api-key" className="block text-sm font-medium">API key</label>
        <input
          id="api-key"
          type="password"
          value={candidate}
          onChange={(event) => setCandidate(event.target.value)}
          autoComplete="off"
          spellCheck={false}
          autoCapitalize="none"
          disabled={session.isLoading}
          aria-describedby={session.error ? 'api-key-error' : undefined}
          className="w-full min-w-0 rounded-md border border-border bg-background px-3 py-2 text-foreground focus-visible:outline-2 focus-visible:outline-ring"
        />
        {session.error && <p id="api-key-error" role="alert" className="text-sm text-destructive">{session.error.message}</p>}
        <button
          type="submit"
          disabled={session.isLoading}
          className="w-full rounded-md bg-primary px-4 py-2 text-primary-foreground disabled:opacity-50"
        >
          {session.isLoading ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
    </main>
  );
}
