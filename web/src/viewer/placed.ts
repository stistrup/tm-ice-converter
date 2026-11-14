import { BufferAttribute, BufferGeometry, Color, Vector3, type InstancedMesh, type Material, type Matrix4 } from 'three'
import type { InstanceInfo } from '../api'
import type { Category } from './categories'

export interface PieceParts {
  geometries: BufferGeometry[]
  materials: Material[]
}

export interface Placed extends PieceParts {
  meshKey: string
  matrix: Matrix4
  category: Category
  // ground and terrain blocks draw their own ground; the base ground shows elsewhere
  ownsGround: boolean
  // block colour: 0 Default (as painted), 1 White .. 5 Black
  tint: number
  // ground blocks cut the terrain around them, custom ones too (they don't own the ground, the game still reshapes it)
  cuts: boolean
  // stacked 1 cm over the same native block: too close for the depth buffer, drawn in front
  stacked: boolean
  // where it sits in the instanced meshes, one per geometry
  slots: { mesh: InstancedMesh; n: number }[]
  // 'b3' / 'i7': block or item index in the map, what the preview's converted pieces point back to
  origin: string | null
}

// a converted piece in the preview layer, by the original it replaces
export interface LayerPiece extends PieceParts {
  key: string
  matrix: Matrix4
}

// a block that draws its own ground; our custom blocks carry none, except on hills and cliffs (BlockNames.OnTerrainShape)
export const ownsGroundOf = (info: InstanceInfo) => {
  const custom = info.kind === 'custom block'
  // made from a terrain block (snowed ground): it is that cell's ground
  const keepsTerrain = custom && (/On[A-Z][a-z]+(Cliff|Hill)/.test(String(info.sourceBlock ?? '')) || /\bterrain\b/.test(info.tags ?? ''))
  return info.kind.includes('block') && (!custom || keepsTerrain) && /\b(ground|terrain)\b/.test(info.tags ?? '')
}

export const originOf =(info: InstanceInfo, block: string, item: string): string | null => typeof info[block] === 'number' ? `b${info[block]}` : typeof info[item] === 'number' ? `i${info[item]}` : null

// the colour index rides in the instance colour's red channel
const tintColor = new Color()
export const tintOf = (index: number) => tintColor.setRGB(index / 8, 0, 0)

export const pieceGeometry = (buffer: ArrayBuffer, offset: number, vertices: number, indices: number, colors: boolean) => {
  const geometry = new BufferGeometry()
  geometry.setAttribute('position', new BufferAttribute(new Float32Array(buffer, offset, vertices * 3), 3))
  geometry.setAttribute('normal', new BufferAttribute(new Float32Array(buffer, offset + vertices * 12, vertices * 3), 3))
  geometry.setAttribute('uv', new BufferAttribute(new Float32Array(buffer, offset + vertices * 24, vertices * 2), 2))
  geometry.setIndex(new BufferAttribute(new Uint32Array(buffer, offset + vertices * 32, indices), 1))
  if (colors) geometry.setAttribute('cv', new BufferAttribute(new Float32Array(buffer, offset + vertices * 32 + indices * 4, vertices * 4), 4))
  return geometry
}

const none = new Vector3(0, 0, 0)

// hidden = scaled to nothing where it stands; three culls an instanced mesh by bounds it works out once, so they go stale here
export const showInstance = (mesh: InstancedMesh, n: number, matrix: Matrix4, shown: boolean) => {
  mesh.setMatrixAt(n, shown ? matrix : matrix.clone().scale(none))
  mesh.instanceMatrix.needsUpdate = true
  mesh.boundingSphere = null
  mesh.boundingBox = null
}
