import { InstancedMesh, Matrix4, Vector3, type BufferGeometry, type Material, type Scene } from 'three'
import { showInstance, tintOf, type PieceParts, type Placed } from './placed'
import { keepUncut, markCutter } from './terrainCut'

// the preview's redraw of one piece: per geometry a material (null = left out), on the whole piece or only on its upward faces
export interface LookChange {
  index: number
  // undefined = as it is (whole piece) / not covered (tops only)
  materials: (Material | null | undefined)[]
  topsOnly: boolean
}

// the conversion preview drawn in its materials: whole pieces swapped (the native one hidden), or the theme laid on their upward faces
export class Looks {
  private meshes: InstancedMesh[] = []
  private hiddenSlots: { mesh: InstancedMesh; n: number; matrix: Matrix4 }[] = []
  private readonly upward = new Map<BufferGeometry, BufferGeometry | null>()
  // the preview's version of a piece, for the properties picture
  private parts = new Map<number, PieceParts>()

  constructor(
    private readonly scene: Scene,
    private readonly geometries: BufferGeometry[],
  ) {}

  of(index: number): PieceParts | undefined {
    return this.parts.get(index)
  }

  set(changes: LookChange[], placedList: Placed[]) {
    for (const { mesh, n, matrix } of this.hiddenSlots) showInstance(mesh, n, matrix, true)
    this.hiddenSlots = []
    for (const mesh of this.meshes) {
      this.scene.remove(mesh)
      mesh.dispose()
    }
    this.meshes = []
    this.parts = new Map()

    const groups = new Map<string, { placed: Placed[]; change: LookChange }>()
    for (const change of changes) {
      const placed = placedList[change.index]
      if (!placed) continue
      if (!change.topsOnly)
        for (const { mesh, n } of placed.slots) {
          this.hiddenSlots.push({ mesh, n, matrix: placed.matrix })
          showInstance(mesh, n, placed.matrix, false)
        }
      // drawn by the changed layer: partsOf takes it from there
      if (change.topsOnly || change.materials.some((m) => m !== null)) this.parts.set(change.index, this.lookOf(placed, change))
      const key = `${placed.meshKey}|${change.topsOnly}|${change.materials.map((m) => m?.uuid ?? '-').join(',')}`
      const group = groups.get(key)
      if (group) group.placed.push(placed)
      else groups.set(key, { placed: [placed], change })
    }
    for (const { placed, change } of groups.values()) {
      placed[0].geometries.forEach((geometry, i) => {
        const swapped = change.materials[i]
        const material = swapped === undefined && !change.topsOnly ? placed[0].materials[i] : swapped
        if (!material) return
        const shape = change.topsOnly ? this.upwardFaces(geometry) : geometry
        if (!shape) return
        const mesh = new InstancedMesh(shape, material, placed.length)
        placed.forEach((p, n) => {
          mesh.setMatrixAt(n, p.matrix)
          mesh.setColorAt(n, tintOf(p.tint))
        })
        mesh.matrixAutoUpdate = false
        if (placed[0].ownsGround && placed[0].category !== 'terrain') markCutter(mesh)
        if (placed[0].category !== 'terrain') keepUncut(mesh)
        this.scene.add(mesh)
        this.meshes.push(mesh)
      })
    }
  }

  private lookOf(placed: Placed, change: LookChange): PieceParts {
    const parts: PieceParts = change.topsOnly
      ? { geometries: [...placed.geometries], materials: [...placed.materials] }
      : { geometries: [], materials: [] }
    placed.geometries.forEach((geometry, i) => {
      const swapped = change.materials[i]
      const material = swapped === undefined && !change.topsOnly ? placed.materials[i] : swapped
      const shape = material && (change.topsOnly ? this.upwardFaces(geometry) : geometry)
      if (!material || !shape) return
      parts.geometries.push(shape)
      parts.materials.push(material)
    })
    return parts
  }

  // the faces pointing up (local), lifted 2 cm so they sit on the native surface
  private upwardFaces(geometry: BufferGeometry): BufferGeometry | null {
    if (this.upward.has(geometry)) return this.upward.get(geometry)!
    const position = geometry.getAttribute('position')
    const index = geometry.getIndex()
    const count = index ? index.count : position.count
    const at = (i: number) => (index ? index.getX(i) : i)
    const [a, b, c, ab, ac] = [new Vector3(), new Vector3(), new Vector3(), new Vector3(), new Vector3()]
    const keep: number[] = []
    for (let i = 0; i + 2 < count; i += 3) {
      a.fromBufferAttribute(position, at(i))
      b.fromBufferAttribute(position, at(i + 1))
      c.fromBufferAttribute(position, at(i + 2))
      const normal = ab.subVectors(b, a).cross(ac.subVectors(c, a))
      // the converter's top minimum (ItemExporters.TopMinY)
      if (normal.lengthSq() > 0 && normal.normalize().y > 0.02) keep.push(at(i), at(i + 1), at(i + 2))
    }
    let shape: BufferGeometry | null = null
    if (keep.length) {
      shape = geometry.clone()
      shape.setIndex(keep)
      shape.translate(0, 0.02, 0)
      this.geometries.push(shape)
    }
    this.upward.set(geometry, shape)
    return shape
  }
}
