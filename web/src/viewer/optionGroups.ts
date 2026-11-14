// options that only make sense together, shown as one choice; each choice sets the converter's own switches
export interface Choice {
  label: string
  set: Record<string, boolean>
}

export interface OptionGroup {
  title: string
  description: string
  options: string[]
  choices: Choice[]
  // which choice the switches add up to, in the converter's order of precedence
  current: (value: (name: string) => boolean) => number
}

export const optionGroups: OptionGroup[] = [
  {
    title: 'Engine-off blocks',
    description: 'Kept — they stay engine-off.\nReset blocks — they become reset blocks.\nPlain ice — they become plain ice.\nEngine-off gates are removed with either change.',
    options: ['engineOffToReset', 'engineOffToPlain'],
    choices: [
      { label: 'Kept', set: { engineOffToReset: false, engineOffToPlain: false } },
      { label: 'Reset blocks', set: { engineOffToReset: true, engineOffToPlain: false } },
      { label: 'Plain ice', set: { engineOffToPlain: true } },
    ],
    current: (v) => (v('engineOffToPlain') ? 2 : v('engineOffToReset') ? 1 : 0),
  },
]

// shown after the other checkboxes
export const terrainOptions = ['fillEmptyWithSnow', 'snowOnWaterHills', 'snowOnCliffs']

export const grouped = new Set([...optionGroups.flatMap((group) => group.options), ...terrainOptions])

// options that lay a cover material; only the theme's snow for now
export const coverOptions = new Set(['snowOnZoneSides', ...terrainOptions])
export const coverMaterials = ['Snow']
