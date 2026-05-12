/**
 * ThemeToggle — three-state theme switcher: light / dark / system.
 *
 * Uses next-themes useTheme() hook. Cycles through the three states on click,
 * or could be a dropdown — current impl is a single cycle button to keep the
 * nav uncluttered. The active icon indicates the current resolved mode.
 *
 * Task: DiVoid #247
 */

import { useTheme } from 'next-themes';
import { Sun, Moon, Monitor } from 'lucide-react';
import { cn } from '@/lib/cn';

const THEME_CYCLE: Array<'light' | 'dark' | 'system'> = ['light', 'dark', 'system'];

const THEME_ICONS = {
  light: Sun,
  dark: Moon,
  system: Monitor,
} as const;

const THEME_LABELS = {
  light: 'Switch to dark theme',
  dark: 'Switch to system theme',
  system: 'Switch to light theme',
} as const;

export function ThemeToggle() {
  const { theme, setTheme } = useTheme();

  const current = (theme as 'light' | 'dark' | 'system') ?? 'system';
  const Icon = THEME_ICONS[current] ?? Monitor;
  const ariaLabel = THEME_LABELS[current] ?? 'Toggle theme';

  function cycleTheme() {
    const idx = THEME_CYCLE.indexOf(current);
    const next = THEME_CYCLE[(idx + 1) % THEME_CYCLE.length];
    setTheme(next);
  }

  return (
    <button
      type="button"
      onClick={cycleTheme}
      aria-label={ariaLabel}
      title={ariaLabel}
      className={cn(
        'flex h-8 w-8 items-center justify-center rounded-md',
        'text-muted-foreground hover:text-foreground hover:bg-muted transition-colors',
      )}
    >
      <Icon size={16} aria-hidden="true" />
    </button>
  );
}
