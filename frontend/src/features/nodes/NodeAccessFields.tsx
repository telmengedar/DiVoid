import { NODE_ACCESS_FLAGS } from './nodeAccess';

interface NodeAccessFieldsProps {
  /** Current integer mask, including any unnamed flags. */
  value: number;
  /** Receives the mask after toggling only the selected known grant. */
  onChange: (value: number) => void;
}

/** Four independent access controls shared by node creation and metadata editing.
 * @param props Current mask and change callback.
 */
export function NodeAccessFields({ value, onChange }: NodeAccessFieldsProps) {
  return (
    <fieldset className="flex flex-col gap-2">
      <legend className="text-sm font-medium mb-1">Access</legend>
      {NODE_ACCESS_FLAGS.map(({ bit, label }) => (
        <label key={bit} className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            className="rounded border-border"
            checked={(value & bit) !== 0}
            onChange={(event) => onChange(event.target.checked ? value | bit : value & ~bit)}
          />
          {label}
        </label>
      ))}
      <p className="text-xs text-muted-foreground">
        Organization grants apply only to members of the node’s assigned organization.
        Write does not include read. The owner and admins retain access.
      </p>
    </fieldset>
  );
}
