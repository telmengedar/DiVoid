/**
 * Authenticated landing page — /
 *
 * Calls GET /api/users/me via useWhoami() and displays:
 *  - User name
 *  - Email
 *  - Permissions list
 *
 * PR 1 scope: minimal render. Visual polish and "recent activity" surface
 * are deferred to later PRs once the read hooks land.
 */

import { useWhoami } from './useWhoami';
import { Shield, Mail, User } from 'lucide-react';

function PermissionBadge({ permission }: { permission: string }) {
  const colours: Record<string, string> = {
    admin: 'bg-destructive/10 text-destructive',
    write: 'bg-primary/10 text-primary',
    read: 'bg-muted text-muted-foreground',
  };

  const colour = colours[permission] ?? 'bg-muted text-muted-foreground';

  return (
    <span
      className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${colour}`}
    >
      {permission}
    </span>
  );
}

export function LandingPage() {
  const { data: user, isLoading, error } = useWhoami();

  if (isLoading) {
    return (
      <div className="flex h-full items-center justify-center">
        <p className="text-muted-foreground text-sm">Loading your profile…</p>
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex h-full items-center justify-center">
        <div className="text-center">
          <p className="text-destructive text-sm font-medium">Failed to load profile</p>
          <p className="text-muted-foreground text-xs mt-1">
            {error instanceof Error ? error.message : 'Unknown error'}
          </p>
        </div>
      </div>
    );
  }

  if (!user) return null;

  return (
    <div className="max-w-lg mx-auto px-4 py-12">
      <h1 className="text-2xl font-medium mb-8">
        Hello, {user.name}.
      </h1>

      <div className="space-y-4">
        {/* Email */}
        {user.email && (
          <div className="flex items-center gap-3 text-sm">
            <Mail size={16} className="text-muted-foreground shrink-0" aria-hidden="true" />
            <span className="text-muted-foreground">{user.email}</span>
          </div>
        )}

        {/* User ID */}
        <div className="flex items-center gap-3 text-sm">
          <User size={16} className="text-muted-foreground shrink-0" aria-hidden="true" />
          <span className="text-muted-foreground">
            User #{user.id}
            {!user.enabled && (
              <span className="ml-2 text-destructive text-xs">(disabled)</span>
            )}
          </span>
        </div>

        {/* Permissions */}
        <div className="flex items-start gap-3 text-sm">
          <Shield size={16} className="text-muted-foreground shrink-0 mt-0.5" aria-hidden="true" />
          <div>
            <span className="text-muted-foreground block mb-2">Permissions</span>
            {user.permissions.length > 0 ? (
              <div className="flex flex-wrap gap-1.5" role="list" aria-label="Your permissions">
                {user.permissions.map((p) => (
                  <span key={p} role="listitem">
                    <PermissionBadge permission={p} />
                  </span>
                ))}
              </div>
            ) : (
              <span className="text-muted-foreground text-xs italic">
                No permissions assigned — contact the admin.
              </span>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
