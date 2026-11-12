import { InstancedMesh, Matrix4, type BufferGeometry, type Material, type Scene, type WebGLRenderer } from 'three'
import type { SceneManifest } from '../api'
import { cellsOf } from './baseGround'
import { SceneMaterials } from './materials'
import { originOf, pieceGeometry, tintOf, type LayerPiece } from './placed'
import { drawOver, markCutter } from './terrainCut'

export interface PreviewLayer {
  meshes: InstancedMesh[]
  materials: SceneMaterials
  // by the original each replaces ('b3', 'i7')
  pieces: Map<string, LayerPiece>
  // ground blocks the conversion added (fillEmptyWithSnow): the base ground goes there
  covered: Set<string>
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
  for (const instance of manifest.instances) {
    const origin = originOf(instance.info, 'originBlock', 'originItem')
    if (!origin && instance.info.kind.includes('block') && /\bground\b/.test(instance.info.tags ?? '')) {
      const cells = cellsOf(pieces[instance.mesh].map((p) => p.geometry), new Matrix4().fromArray(instance.m))
      if (cells) for (let x = cells.x0; x <= cells.x1; x++) for (let z = cells.z0; z <= cells.z1; z++) covered.add(`${x},${z}`)
    }
    if (origin && !byOrigin.has(origin))
      byOrigin.set(origin, {
        key: `layer:${instance.mesh}`,
        geometries: pieces[instance.mesh].map((p) => p.geometry),
        materials: pieces[instance.mesh].map((p) => p.material),
        matrix: new Matrix4().fromArray(instance.m),
      })
    const key = `${instance.mesh}|${instance.info.stacked === true}`
    byMesh.set(key, [...(byMesh.get(key) ?? []), instance])
  }
  for (const list of byMesh.values()) {
    for (const { geometry, material } of pieces[list[0].mesh]) {
      const mesh = new InstancedMesh(geometry, material, list.length)
      list.forEach((instance, n) => {
        mesh.setMatrixAt(n, new Matrix4().fromArray(instance.m))
        mesh.setColorAt(n, tintOf(typeof instance.info.color === 'number' ? instance.info.color : 0))
      })
      mesh.matrixAutoUpdate = false
      const info = list[0].info
      if (/\bground\b/.test(info.tags ?? '') && !/\bterrain\b/.test(info.tags ?? '')) markCutter(mesh)
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
