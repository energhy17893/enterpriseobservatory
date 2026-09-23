import { useState } from 'react'
import { Card } from '@/components/Primitives'
import { getThemeSet, setThemeSet, THEME_SETS, type ThemeSet } from '@/lib/theme'

const DESCRIPTION: Record<ThemeSet, string> = {
  classic: 'The original colours.',
  observatory: 'The revised colours. Currently the same as Classic; it changes as the redesign lands.',
}

/**
 * The token set this browser shows. Colours only: every screen, its layout
 * and its wording are the same in both (eo-ux §8, ADR-0007).
 */
export function Appearance() {
  const [current, setCurrent] = useState(getThemeSet)

  return (
    <div className="space-y-4">
      <h1 className="text-xl font-semibold">Appearance</h1>

      <Card className="p-4">
        <fieldset className="space-y-3">
          <legend className="font-medium">Theme</legend>
          <p className="text-sm text-muted-foreground">
            Saved in this browser only. Other people and other browsers are not affected.
          </p>
          {THEME_SETS.map((set) => (
            <label key={set} className="flex cursor-pointer items-start gap-2">
              <input
                type="radio"
                name="theme-set"
                value={set}
                checked={current === set}
                onChange={() => {
                  setThemeSet(set)
                  setCurrent(set)
                }}
                className="mt-1 accent-primary"
              />
              <span>
                <span className="block text-sm font-medium capitalize">{set}</span>
                <span className="block text-sm text-muted-foreground">{DESCRIPTION[set]}</span>
              </span>
            </label>
          ))}
        </fieldset>
      </Card>
    </div>
  )
}
