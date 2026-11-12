import type { PreviewResult } from '../api'

// outcomes drawn in the converter's materials, with the material table's prefix for that kind of piece
export const lookPrefix: Record<string, string> = { custom: '', twin: '', item: '', skin: 'skin:', ice: 'item:', tops: 'top:' }

// the converter ices only these on the upward faces (BlockNames.IsWallMaterial)
export const wallMaterials = new Set(['TrackWall', 'TrackWallClips', 'Structure'])

// as ItemMaterials reads data/themes/ice/materials.jsonc: "<block pattern>|name" wins for its blocks, then prefix + name, then the name
export const themeMaterialName = (swaps: PreviewResult['swaps'], name: string, block: string, prefix: string): string | undefined => {
  for (const [key, to] of Object.entries(swaps)) {
    const bar = key.indexOf('|')
    if (bar < 0 || key.slice(bar + 1) !== name) continue
    const pattern = new RegExp('^' + key.slice(0, bar).replace(/[.+?^${}()[\]\\]/g, '\\$&').replace(/\*/g, '.*') + '$')
    if (pattern.test(block)) return to
  }
  if (prefix && swaps[prefix + name] !== undefined) return swaps[prefix + name]
  return swaps[name]
}
