/**
 * K3: how a control's citation is shown -- labelled "basis:" (not "source:",
 * which on these screens means the catalogue: Broadcom SCG or eo-continuity).
 *
 * Never hidden: a control that cites nothing rests on the product's own
 * policy and says so. Same words as the CSV and the mailed report
 * (Api/Reports/ControlBasis.cs).
 */
export const NO_CITATION = 'no citation — product policy'

export function basisLabel(citation: string | null | undefined): string {
  return citation && citation.trim().length > 0 ? citation : NO_CITATION
}
