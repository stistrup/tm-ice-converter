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
    title: 'Blocks become',
    description:
      'Custom block — an ice copy of the block; closest to the original look.\nItem — an ice item in its place; ground blocks keep the original under an ice skin.\nIce skin — the original stays, with a thin ice layer on top; smallest maps.',
    options: ['customBlocks', 'skinsOnly'],
    choices: [
      { label: 'Custom block', set: { customBlocks: true, skinsOnly: false } },
      { label: 'Item', set: { customBlocks: false, skinsOnly: false } },
      { label: 'Ice skin (slightly raised)', set: { skinsOnly: true } },
    ],
    current: (v) => (v('skinsOnly') ? 2 : v('customBlocks') ? 0 : 1),
  },
  {
    title: 'Start, checkpoints, finish',
    description: 'Like other blocks — as chosen in \u201cBlocks become\u201d.\nItem — an ice item, even when blocks become custom blocks.\nIce skin — the original stays under an ice skin; otherwise these are the largest custom blocks.',
    options: ['customWaypoints', 'skinWaypoints'],
    choices: [
      { label: 'Like other blocks', set: { customWaypoints: true, skinWaypoints: false } },
      { label: 'Item', set: { customWaypoints: false, skinWaypoints: false } },
      { label: 'Ice skin (slightly raised)', set: { skinWaypoints: true } },
    ],
    current: (v) => (v('skinWaypoints') ? 2 : v('customWaypoints') ? 0 : 1),
  },
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

export const grouped = new Set(optionGroups.flatMap((group) => group.options))
