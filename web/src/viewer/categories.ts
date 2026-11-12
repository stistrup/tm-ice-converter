import type { InstanceInfo } from '../api'

export const categories = ['block', 'customBlock', 'iceItem', 'item', 'terrain', 'missing'] as const
export type Category = (typeof categories)[number]

export const categoryLabels: Record<Category, string> = {
  block: 'Blocks',
  customBlock: 'Custom blocks',
  iceItem: 'Ice items',
  item: 'Items',
  terrain: 'Terrain',
  missing: 'No mesh',
}

const terrainNames = /^(Dirt|Water|DecoLake|DecoTerrain|Land|Beach|Sea)/

export const categoryOf = (info: InstanceInfo): Category => {
  if (info.units || info.placeholder) {
    return info.units && terrainNames.test(info.name) ? 'terrain' : 'missing'
  }
  if (info.tags?.split(', ').includes('terrain')) return 'terrain'
  if (info.kind === 'custom block') return 'customBlock'
  if (info.kind === 'ice item') return 'iceItem'
  return info.kind === 'item' ? 'item' : 'block'
}
