import { InstancedMesh, Matrix4, type BufferGeometry, type Material, type Scene, type WebGLRenderer } from 'three'
import type { SceneManifest } from '../api'
import { groundCells } from './baseGround'
import { SceneMaterials } from './materials'
import { originOf, ownsGroundOf, pieceGeometry, showInstance, tintOf, type LayerPiece } from './placed'
import { drawOver, isTerrainShaped, markCutter } from './terrainCut'

export interface PreviewLayer {
  meshes: InstancedMesh[]
  materials: SceneMaterials
  // by the original each replaces ('b3', 'i7')
  pieces: Map<string, LayerPiece>
  // ground blocks the conversion added (fillEmptyWithSnow): the base ground goes there
  covered: Set<string>
  // hides the converted parts of these originals (their new version is on its way), shows the rest
  hide: (origins: Set<string>) => void
  dispose: () => void
}

// textured before it shows
export const buildLayer = async (
  { manifest, geometry: buffer }: { manifest: SceneManifest; geometry: ArrayBuffer },
  scene: Scene,
  renderer: WebGLRenderer,
  shared: SceneMaterials | null,
  invalidate: () => void,
): Promise<PreviewLayer> => {
  const materials = new SceneMaterials(manifest.materials, renderer, invalidate, manifest.environment, shared)
  const geometries: BufferGeometry[] = []
  const meshes: InstancedMesh[] = []
  const pieces = manifest.meshes.map((mesh) =>
    mesh.pieces.map((piece) => {
      const geometry = pieceGeometry(buffer, piece.offset, piece.vertices, piece.indices, piece.colors)
      geometries.push(geometry)
      return { geometry, material: materials.materials[piece.material] as Material }
    }),
  )
  const byMesh = new Map<string, typeof manifest.instances>()
  const byOrigin = new Map<string, LayerPiece>()
  const covered = new Set<string>()
  const slots = new Map<string, { mesh: InstancedMesh; n: number; matrix: Matrix4 }[]>()
  for (const instance of manifest.instances) {
    const origin = originOf(instance.info, 'originBlock', 'originItem')
    // converted and added pieces own their ground as the originals do (the hidden ones give theirs up)
    if (ownsGroundOf(instance.info))
      for (const key of groundCells(pieces[instance.mesh].map((p) => p.geometry), new Matrix4().fromArray(instance.m))) covered.add(key)
    if (origin) {
      const matrix = new Matrix4().fromArray(instance.m)
      const known = byOrigin.get(origin)
      if (!known)
        byOrigin.set(origin, {
          key: `layer:${instance.mesh}`,
          geometries: pieces[instance.mesh].map((p) => p.geometry),
          materials: pieces[instance.mesh].map((p) => p.material),
          matrix,
        })
      else {
        // more parts of one piece (a custom block over its kept native body): in the first part's space
        const relative = matrix.equals(known.matrix) ? null : known.matrix.clone().invert().multiply(matrix)
        known.key += `+${instance.mesh}`
        for (const { geometry, material } of pieces[instance.mesh]) {
          const shape = relative ? geometry.clone().applyMatrix4(relative) : geometry
          if (relative) geometries.push(shape)
          known.geometries.push(shape)
          known.materials.push(material)
        }
      }
    }
    const key = `${instance.mesh}|${instance.info.stacked === true}`
    byMesh.set(key, [...(byMesh.get(key) ?? []), instance])
  }
  for (const list of byMesh.values()) {
    for (const { geometry, material } of pieces[list[0].mesh]) {
      const mesh = new InstancedMesh(geometry, material, list.length)
      list.forEach((instance, n) => {
        const matrix = new Matrix4().fromArray(instance.m)
        mesh.setMatrixAt(n, matrix)
        const origin = originOf(instance.info, 'originBlock', 'originItem')
        if (origin) slots.set(origin, [...(slots.get(origin) ?? []), { mesh, n, matrix }])
        mesh.setColorAt(n, tintOf(typeof instance.info.color === 'number' ? instance.info.color : 0))
      })
      mesh.matrixAutoUpdate = false
      const info = list[0].info
      if (/\bground\b/.test(info.tags ?? '') && !/\bterrain\b/.test(info.tags ?? '') && !isTerrainShaped(pieces[list[0].mesh].map((p) => p.material))) markCutter(mesh)
      if (info.stacked === true) drawOver(mesh)
      meshes.push(mesh)
    }
  }
  await materials.loadTextures()
  return {
    meshes,
    materials,
    pieces: byOrigin,
    covered,
    hide: (origins) => {
      for (const [origin, list] of slots)
        for (const { mesh, n, matrix } of list) showInstance(mesh, n, matrix, !origins.has(origin))
      invalidate()
    },
    dispose: () => {
      for (const mesh of meshes) {
        scene.remove(mesh)
        mesh.dispose()
      }
      for (const geometry of geometries) geometry.dispose()
      materials.dispose()
      invalidate()
    },
  }
}
