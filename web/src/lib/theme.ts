/**
 * Which token set this viewer sees (eo-ux §8).
 *
 * Visual only: the set changes colour values through `data-theme-set` on
 * <html>; layout, navigation, paging and copy are shared code and never
 * depend on it (ADR-0007). Per viewer, in this browser: storage can be
 * blocked or empty (private window, cleared site data), so every access is
 * guarded and the default is classic.
 */
export const THEME_SETS = ['classic', 'observatory'] as const
export type ThemeSet = (typeof THEME_SETS)[number]

const KEY = 'eo.themeSet'

export function getThemeSet(): ThemeSet {
  try {
    const stored = localStorage.getItem(KEY)
    return THEME_SETS.find((s) => s === stored) ?? 'classic'
  } catch {
    return 'classic'
  }
}

export function applyThemeSet(set: ThemeSet) {
  document.documentElement.dataset.themeSet = set
}

export function setThemeSet(set: ThemeSet) {
  applyThemeSet(set)
  try {
    localStorage.setItem(KEY, set)
  } catch {
    // Not remembered in this browser; the choice still holds until reload.
  }
}
