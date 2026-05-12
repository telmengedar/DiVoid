/**
 * Top navigation bar.
 *
 * Renders:
 *  - App name / logo
 *  - Mode-switch tabs (Workspace / Tasks) — stubs in PR 1
 *  - User avatar dropdown (logout)
 *
 * Design: docs/architecture/frontend-bootstrap.md §9.5
 */

import { NavLink } from 'react-router-dom';
import { useAuth } from 'react-oidc-context';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { LogOut, Network, ListTodo, Search } from 'lucide-react';
import { cn } from '@/lib/cn';
import { ROUTES } from '@/lib/constants';

function UserMenuTrigger({ name }: { name?: string }) {
  const initials = name
    ? name
        .split(' ')
        .map((part) => part[0])
        .join('')
        .toUpperCase()
        .slice(0, 2)
    : '?';

  return (
    <button
      className="flex h-8 w-8 items-center justify-center rounded-full bg-muted text-xs font-medium hover:opacity-80 transition-opacity"
      aria-label={`User menu for ${name ?? 'current user'}`}
    >
      {initials}
    </button>
  );
}

export function TopNav() {
  const auth = useAuth();
  const userName = auth.user?.profile?.name ?? auth.user?.profile?.preferred_username;

  const navLinks = [
    { to: ROUTES.SEARCH, label: 'Search', Icon: Search },
    { to: ROUTES.WORKSPACE, label: 'Workspace', Icon: Network },
    { to: ROUTES.TASKS, label: 'Tasks', Icon: ListTodo },
  ];

  return (
    <header className="h-12 border-b border-border flex items-center px-4 gap-4 shrink-0">
      {/* App name */}
      <NavLink
        to={ROUTES.HOME}
        className="font-medium text-sm text-foreground hover:text-foreground/80 transition-colors"
        aria-label="DiVoid home"
      >
        DiVoid
      </NavLink>

      {/* Separator */}
      <div className="w-px h-5 bg-border" role="separator" aria-hidden="true" />

      {/* Mode-switch tabs */}
      <nav aria-label="Application modes" className="flex items-center gap-1">
        {navLinks.map(({ to, label, Icon }) => (
          <NavLink
            key={to}
            to={to}
            className={({ isActive }) =>
              cn(
                'inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md text-xs font-medium transition-colors',
                isActive
                  ? 'bg-accent text-accent-foreground'
                  : 'text-muted-foreground hover:text-foreground hover:bg-muted',
              )
            }
          >
            <Icon size={14} aria-hidden="true" />
            {label}
          </NavLink>
        ))}
      </nav>

      {/* Spacer */}
      <div className="flex-1" />

      {/* User menu */}
      {auth.isAuthenticated && (
        <DropdownMenu.Root>
          <DropdownMenu.Trigger asChild>
            <UserMenuTrigger name={userName} />
          </DropdownMenu.Trigger>

          <DropdownMenu.Portal>
            <DropdownMenu.Content
              align="end"
              sideOffset={8}
              className="min-w-40 rounded-md border border-border bg-popover p-1 shadow-md text-sm"
            >
              {userName && (
                <>
                  <div className="px-2 py-1.5 text-xs text-muted-foreground truncate max-w-40">
                    {userName}
                  </div>
                  <DropdownMenu.Separator className="my-1 -mx-1 h-px bg-border" />
                </>
              )}

              <DropdownMenu.Item
                className="flex items-center gap-2 px-2 py-1.5 rounded cursor-pointer text-sm outline-none hover:bg-muted focus:bg-muted text-destructive"
                onSelect={() => auth.signoutRedirect()}
              >
                <LogOut size={14} aria-hidden="true" />
                Sign out
              </DropdownMenu.Item>
            </DropdownMenu.Content>
          </DropdownMenu.Portal>
        </DropdownMenu.Root>
      )}
    </header>
  );
}
