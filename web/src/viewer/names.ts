import type { InstanceInfo } from '../api'

// RoadTechCurve1, not IceConverter\gIce_RoadTechCurve1_Ice_BlueBay_none.Block.Gbx_CustomBlock
// '' for an index from the scene before (a hover can land while the next map loads)
export function displayName(info: InstanceInfo | undefined): string {
  if (!info) return ''
  if (typeof info.sourceBlock === 'string') return info.sourceBlock
  return info.name.split('\\').pop()!.replace(/\.(Item|Block)\.gbx.*$/i, '')
}

const labels: Record<string, string> = {
  kind: 'Type',
  color: 'Colour',
  name: 'File',
  sourceBlock: 'Made from',
  drawnAs: 'Drawn as',
  variant: 'Variant',
  shownVariant: 'Shown variant',
  mobil: 'Mobil',
  tags: 'Tags',
  flags: 'Flags',
}

const hints: Record<string, string> = {
  variant: "Which of the block's variants is placed (bits 21-26 of the flags); blocks come in ground and air variants",
  mobil: 'Which look of that variant the game uses (bits 0-11 of the flags), e.g. a different pillar or frame',
  flags: 'Raw block flags from the map file: variant and mobil, plus bits such as ground, ghost and free placement',
}

function kindLabel(kind: string) {
  return kind[0].toUpperCase() + kind.slice(1)
}

// the editor's block colour (DifficultyColor); Default = as painted
const colourNames = ['Default', 'White', 'Green', 'Blue', 'Red', 'Black']

const order = Object.keys(labels)
// keys without a label go last
const rank = (key: string) => (order.includes(key) ? order.indexOf(key) : order.length)
const hiddenKeys = new Set(['placeholder', 'cell', 'position', 'direction', 'units', 'block', 'item'])

export function infoRows(info: InstanceInfo): { key: string; label: string; value: string; hint?: string }[] {
  return Object.entries(info)
    .filter(([key]) => !hiddenKeys.has(key))
    .filter(([key]) => key !== 'name' || displayName(info) !== info.name)
    .sort(([a], [b]) => rank(a) - rank(b))
    .map(([key, value]) => ({
      key,
      label: labels[key] ?? key,
      hint: hints[key],
      value:
        key === 'kind'
          ? kindLabel(String(value))
          : key === 'color'
            ? (colourNames[Number(value)] ?? String(value))
            : Array.isArray(value)
              ? value.join(', ')
              : String(value),
    }))
}
