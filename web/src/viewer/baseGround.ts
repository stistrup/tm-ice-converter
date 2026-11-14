import { Box3, BufferAttribute, BufferGeometry, Vector3, type Matrix4 } from 'three'
import { groundHeight, isWaterZone, waterHeight } from './materialRules'
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

// m² of upward faces a cell needs: a wall's top edge reaching into the next cell (pillar clips) doesn't count
const GROUND_AREA = 64

// the cells a ground piece has ground in: those its upward faces cover, not its whole box (beach deco blocks leave cells to the sea)
export const groundCells = (geometries: BufferGeometry[], matrix: Matrix4): Set<string> => {
  const area = new Map<string, number>()
  const [a, b, c, ab, ac] = [new Vector3(), new Vector3(), new Vector3(), new Vector3(), new Vector3()]
  for (const geometry of geometries) {
    const position = geometry.getAttribute('position')
    const index = geometry.getIndex()
    const count = index ? index.count : position.count
    const at = (i: number) => (index ? index.getX(i) : i)
    for (let i = 0; i + 2 < count; i += 3) {
      a.fromBufferAttribute(position, at(i)).applyMatrix4(matrix)
      b.fromBufferAttribute(position, at(i + 1)).applyMatrix4(matrix)
      c.fromBufferAttribute(position, at(i + 2)).applyMatrix4(matrix)
      const normal = ab.subVectors(b, a).cross(ac.subVectors(c, a))
      // its area seen from above
      const flat = Math.abs(normal.y) / 2
      if (flat === 0 || normal.normalize().y < 0.3) continue
      // the middle and each corner a little inward share it: a large face spans several cells
      const [mx, mz] = [(a.x + b.x + c.x) / 3, (a.z + b.z + c.z) / 3]
      for (const [px, pz] of [[mx, mz], [a.x, a.z], [b.x, b.z], [c.x, c.z]]) {
        const key = `${Math.floor((px + (mx - px) * 0.05) / CELL.x)},${Math.floor((pz + (mz - pz) * 0.05) / CELL.z)}`
        area.set(key, (area.get(key) ?? 0) + flat / 4)
      }
    }
  }
  return new Set([...area].filter(([, m2]) => m2 >= GROUND_AREA).map(([key]) => key))
}

// cells without a ground or terrain block show the environment's base ground; covered: cells the preview fills,
// hidden: pieces the preview takes away (their ground goes with them). extent: what the map spans, for the cut pass
export const baseGround = (placed: Placed[], environment: string | null, zone: string | null, covered: Iterable<string>, hidden: ReadonlySet<number> = new Set()): { geometry: BufferGeometry | null; extent: Extent } => {
  const cell = CELL.x
  const owned = new Set<string>(covered)
  let [minX, maxX, minZ, maxZ] = [0, 48, 0, 48]
  for (const [index, piece] of placed.entries()) {
    const cells = cellsOf(piece.geometries, piece.matrix)
    if (!cells) continue
    const { x0, x1, z0, z1 } = cells
    ;[minX, maxX, minZ, maxZ] = [Math.min(minX, x0), Math.max(maxX, x1 + 1), Math.min(minZ, z0), Math.max(maxZ, z1 + 1)]
    if (!piece.ownsGround || hidden.has(index)) continue
    for (const key of groundCells(piece.geometries, piece.matrix)) owned.add(key)
  }
  const positions: number[] = []
  // exact: it only fills cells no block owns, so nothing shares its plane
  const y = isWaterZone(zone) ? waterHeight : groundHeight(environment)
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
