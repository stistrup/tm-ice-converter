import { Box3, BufferAttribute, BufferGeometry, type Matrix4 } from 'three'
import { groundHeight } from './materialRules'
import type { Placed } from './placed'
import type { Extent } from './terrainCut'

export const CELL = { x: 32, y: 8, z: 32 }

// the cells a piece covers, a hair inside so it doesn't claim the cells it only touches
export const cellsOf = (geometries: BufferGeometry[], matrix: Matrix4) => {
  const box = new Box3()
  const part = new Box3()
  for (const geometry of geometries) {
    if (!geometry.boundingBox) geometry.computeBoundingBox()
    box.union(part.copy(geometry.boundingBox!).applyMatrix4(matrix))
  }
  if (box.isEmpty()) return null
  const cell = CELL.x
  return {
    x0: Math.floor((box.min.x + 0.5) / cell), x1: Math.floor((box.max.x - 0.5) / cell),
    z0: Math.floor((box.min.z + 0.5) / cell), z1: Math.floor((box.max.z - 0.5) / cell),
  }
}

// cells without a ground or terrain block show the environment's base ground; covered: cells the preview fills.
// extent: what the map spans, for the cut pass
export const baseGround = (placed: Placed[], environment: string | null, covered: Iterable<string>): { geometry: BufferGeometry | null; extent: Extent } => {
  const cell = CELL.x
  const owned = new Set<string>(covered)
  let [minX, maxX, minZ, maxZ] = [0, 48, 0, 48]
  for (const piece of placed) {
    const cells = cellsOf(piece.geometries, piece.matrix)
    if (!cells) continue
    const { x0, x1, z0, z1 } = cells
    ;[minX, maxX, minZ, maxZ] = [Math.min(minX, x0), Math.max(maxX, x1 + 1), Math.min(minZ, z0), Math.max(maxZ, z1 + 1)]
    if (!piece.ownsGround) continue
    for (let x = x0; x <= x1; x++) for (let z = z0; z <= z1; z++) owned.add(`${x},${z}`)
  }
  const positions: number[] = []
  // exact: it only fills cells no block owns, so nothing shares its plane
  const y = groundHeight(environment)
  for (let x = minX; x < maxX; x++) {
    for (let z = minZ; z < maxZ; z++) {
      if (owned.has(`${x},${z}`)) continue
      const [ax, bx, az, bz] = [x * cell, (x + 1) * cell, z * cell, (z + 1) * cell]
      positions.push(ax, y, az, ax, y, bz, bx, y, bz, ax, y, az, bx, y, bz, bx, y, az)
    }
  }
  const extent = { minX: minX * cell, maxX: maxX * cell, minZ: minZ * cell, maxZ: maxZ * cell }
  if (positions.length === 0) return { geometry: null, extent }
  const geometry = new BufferGeometry()
  geometry.setAttribute('position', new BufferAttribute(new Float32Array(positions), 3))
  geometry.setAttribute('normal', new BufferAttribute(new Float32Array(positions.length).map((_, i) => (i % 3 === 1 ? 1 : 0)), 3))
  geometry.setAttribute('uv', new BufferAttribute(new Float32Array((positions.length / 3) * 2), 2))
  return { geometry, extent }
}
