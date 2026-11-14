import type { InstanceInfo, PreviewDecision, PreviewResult, SceneManifest } from '../api'
import type { LookChange } from './looks'
import type { MapScene, PreviewDraw } from './MapScene'
import { displayName } from './names'
import { outcomes } from './outcomes'
import { lookPrefix, themeMaterialName, wallMaterials } from './themeLook'

// changed pieces drawn as converted where their meshes exist (originals hidden), else in the converter's own materials;
// what's left (swapped, sunk, removed without a mesh) keeps a colour
export const previewDraw = (
  view: MapScene,
  result: PreviewResult,
  decisions: (PreviewDecision | undefined)[],
  infos: InstanceInfo[],
  changed: { manifest: SceneManifest; geometry: ArrayBuffer } | null,
  waiting: Set<number>,
): PreviewDraw => {
  const groups = new Map<number, number[]>()
  const looks: LookChange[] = []
  const hiddenBlocks = new Set(result.hiddenBlocks)
  const hiddenItems = new Set(result.hiddenItems)
  const pendingBlocks = new Set(result.pendingBlocks)
  const pendingItems = new Set(result.pendingItems)
  decisions.forEach((decision, index) => {
    const info = infos[index]
    const pending = waiting.has(index) || (typeof info?.block === 'number' ? pendingBlocks.has(info.block) : typeof info?.item === 'number' && pendingItems.has(info.item))
    if (pending) {
      looks.push({ index, materials: view.materialNames(index).map(() => view.pendingMaterial), topsOnly: false })
      return
    }
    if (decision?.outcome === 'removed') {
      looks.push({ index, materials: view.materialNames(index).map(() => view.ghostMaterial), topsOnly: false })
      return
    }
    const real = typeof info?.block === 'number' ? hiddenBlocks.has(info.block) : typeof info?.item === 'number' && hiddenItems.has(info.item)
    if (real) {
      looks.push({ index, materials: view.materialNames(index).map(() => null), topsOnly: false })
      return
    }
    if (!decision) return
    const prefix = lookPrefix[decision.outcome]
    if (prefix !== undefined) {
      const topsOnly = decision.outcome === 'tops'
      const block = displayName(info)
      const materials = view.materialNames(index).map((name) => {
        if (!name || (topsOnly && !wallMaterials.has(name))) return undefined
        const to = themeMaterialName(result.swaps, name, block, prefix)
        if (to === undefined || to === 'native') return topsOnly ? null : undefined
        if (to === '') return null
        return result.looks[to] ? view.lookMaterial(result.looks[to]) : undefined
      })
      looks.push({ index, materials, topsOnly })
      return
    }
    const color = outcomes[decision.outcome]?.color
    if (color == null) return
    const group = groups.get(color)
    if (group) group.push(index)
    else groups.set(color, [index])
  })
  return { changed, looks, tints: [...groups].map(([color, indices]) => ({ color, indices })), waiting: [...waiting] }
}

// before the first result: only the pieces the run is working on
export const waitingDraw = (view: MapScene, waiting: Set<number>): PreviewDraw => ({
  changed: null,
  looks: [...waiting].map((index) => ({ index, materials: view.materialNames(index).map(() => view.pendingMaterial), topsOnly: false })),
  tints: [],
  waiting: [...waiting],
})
