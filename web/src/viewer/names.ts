import type { InstanceInfo } from '../api'

// RoadTechCurve1, not IceConverter\RoadTechCurve1_Ice_BlueBay_none.Block.Gbx_CustomBlock
export function displayName(info: InstanceInfo): string {
  if (typeof info.sourceBlock === 'string') return info.sourceBlock
  return info.name.split('\\').pop()!.replace(/\.(Item|Block)\.gbx.*$/i, '')
}

const labels: Record<string, string> = {
  kind: 'Type',
  name: 'File',
  sourceBlock: 'Made from',
  drawnAs: 'Drawn as',
  cell: 'Cell',
  position: 'Position',
  direction: 'Direction',
  variant: 'Variant',
  shownVariant: 'Shown variant',
  mobil: 'Mobil',
  units: 'Size (cells)',
  tags: 'Tags',
  flags: 'Flags',
}

function kindLabel(kind: string) {
  return kind[0].toUpperCase() + kind.slice(1)
}

const order = Object.keys(labels)
const hiddenKeys = new Set(['placeholder'])

export function infoRows(info: InstanceInfo): { key: string; label: string; value: string }[] {
  return Object.entries(info)
    .filter(([key]) => !hiddenKeys.has(key))
    .filter(([key]) => key !== 'name' || displayName(info) !== info.name)
    .sort(([a], [b]) => order.indexOf(a) - order.indexOf(b))
    .map(([key, value]) => ({
      key,
      label: labels[key] ?? key,
      value: key === 'kind' ? kindLabel(String(value)) : Array.isArray(value) ? value.join(', ') : String(value),
    }))
}
