import { BufferAttribute, BufferGeometry, Color, type InstancedMesh, type Material, type Matrix4 } from 'three'
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

export const originOf = (info: InstanceInfo, block: string, item: string): string | null => typeof info[block] === 'number' ? `b${info[block]}` : typeof info[item] === 'number' ? `i${info[item]}` : null

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
