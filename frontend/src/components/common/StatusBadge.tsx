/**
 * StatusBadge — theme-aware status pill.
 *
 * Extracted from NodeDetailPage.tsx (task #281 partial, Wiki W1).
 * Used in NodeDetailPage metadata table, WikiSideNav rows, and optional
 * WikiLayout right metadata strip.
 *
 * Colours match the DiVoid node status vocabulary:
 *  open        → emerald
 *  in-progress → blue
 *  closed      → muted
 *  new         → amber
 *  fixed       → purple
 *  (unknown)   → muted
 */

const colorMap: Record<string, string> = {
  open: 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900/30 dark:text-emerald-400',
  'in-progress': 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400',
  closed: 'bg-muted text-muted-foreground',
  new: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400',
  fixed: 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-400',
};

interface StatusBadgeProps {
  status: string | null;
}

export function StatusBadge({ status }: StatusBadgeProps) {
  if (!status) return <span className="text-muted-foreground">—</span>;

  const classes = colorMap[status] ?? 'bg-muted text-muted-foreground';

  return (
    <span
      className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${classes}`}
    >
      {status}
    </span>
  );
}
