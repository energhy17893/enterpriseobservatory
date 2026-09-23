/**
 * Display order for a screen showing every compliance catalogue: Broadcom's
 * own edition(s) first (the audit-standard document), then every product
 * catalogue in the order the API registered them. Owner is used only for
 * ordering -- the label shown for a catalogue is always its own `name`,
 * never a value derived from `owner` (a fixed two-value label collapsed
 * eo-simplivity and eo-bestpractice into "eo-continuity" -- see the
 * fix/ux-catalogue-labels commit that removed it).
 */
export function orderCatalogues<T extends { owner: string }>(catalogues: T[]): T[] {
  const broadcom = catalogues.filter((c) => c.owner === 'Broadcom')
  const rest = catalogues.filter((c) => c.owner !== 'Broadcom')
  return [...broadcom, ...rest]
}
