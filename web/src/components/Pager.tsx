// UI copy is English (eo-ux §6), so the grouping is too: "1,101", not the
// browser locale's "1.101".
const n = (value: number) => value.toLocaleString('en-US')

/**
 * "a–b of total" with Previous/Next — the A1 pager, shared by Alerts, Events
 * and Entities. Real pagination, not infinite scroll (reference §11.7, NN/g:
 * position memory for goal-directed search). The caller keeps `offset` in the
 * URL so a page survives a reload or a shared link.
 */
export function Pager({
  offset,
  pageSize,
  total,
  onOffset,
  unit,
}: {
  offset: number
  pageSize: number
  total: number
  onOffset: (offset: number) => void
  /** Plural noun for the range and the button names, e.g. "groups" or "entities". */
  unit?: string
}) {
  const suffix = unit ? ` ${unit}` : ''
  return (
    <div className="flex items-center gap-2 text-sm text-muted-foreground">
      {/* WCAG 4.1.3: the range changes on every page/filter change and
          must be announced without the operator having to look. */}
      <span className="tabular" role="status" aria-live="polite">
        {total === 0
          ? '0 of 0'
          : `${n(offset + 1)}–${n(Math.min(offset + pageSize, total))} of ${n(total)}${suffix}`}
      </span>
      <button
        type="button"
        aria-label={`Previous page${suffix && ` of${suffix}`}`}
        disabled={offset === 0}
        onClick={() => onOffset(Math.max(0, offset - pageSize))}
        className="rounded-md border border-border px-2 py-1 text-xs disabled:opacity-40"
      >
        Previous
      </button>
      <button
        type="button"
        aria-label={`Next page${suffix && ` of${suffix}`}`}
        disabled={offset + pageSize >= total}
        onClick={() => onOffset(offset + pageSize)}
        className="rounded-md border border-border px-2 py-1 text-xs disabled:opacity-40"
      >
        Next
      </button>
    </div>
  )
}
