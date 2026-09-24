/**
 * RefinementBadge — neutral pill for the open-vocabulary `refinement` field.
 *
 * Unlike StatusBadge, this never keys styling off the value: `refinement` has
 * no enum or allow-list anywhere in the system (DiVoid #14810), so a value this
 * component has never seen must render identically to one it has. The single
 * fixed style is what makes an unrecognised value "fall out" safely — there is
 * no lookup to miss.
 */

interface RefinementBadgeProps {
  refinement: string | null | undefined;
}

export function RefinementBadge({ refinement }: RefinementBadgeProps) {
  if (!refinement) return <span className="text-muted-foreground">—</span>;

  return (
    <span className="inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium bg-violet-100 text-violet-800 dark:bg-violet-900/30 dark:text-violet-400">
      {refinement}
    </span>
  );
}
