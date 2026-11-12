import { DoubleSide, InstancedMesh, MeshBasicMaterial, type BufferGeometry, type Material, type Matrix4, type Scene } from 'three'

// see-through colour over pieces: hover, selection, the preview's groups
export const highlightMaterial = (color: number, opacity: number) => {
  return new MeshBasicMaterial({
    color,
    opacity,
    transparent: true,
    depthWrite: false,
    side: DoubleSide,
    polygonOffset: true,
    polygonOffsetFactor: -2,
    polygonOffsetUnits: -2,
    fog: false,
  })
}

// instance colours carry the block colour index, not a colour: left out, or the ghost turns black
export const ghostMaterial = () => {
  const material = new MeshBasicMaterial({ color: 0xffffff, opacity: 0.18, transparent: true, depthWrite: false, fog: false })
  material.onBeforeCompile = (shader) => {
    shader.fragmentShader = shader.fragmentShader.replace('#include <color_fragment>', '')
  }
  return material
}

export interface Shown {
  key: string
  geometries: BufferGeometry[]
  matrix: Matrix4
}

export const replaceOverlay = (scene: Scene, old: InstancedMesh[], shown: Shown[], material: Material): InstancedMesh[] => {
  for (const mesh of old) {
    scene.remove(mesh)
    mesh.dispose()
  }
  const groups = new Map<string, Shown[]>()
  for (const piece of shown) {
    const group = groups.get(piece.key)
    if (group) group.push(piece)
    else groups.set(piece.key, [piece])
  }
  const overlay: InstancedMesh[] = []
  for (const group of groups.values()) {
    for (const geometry of group[0].geometries) {
      const mesh = new InstancedMesh(geometry, material, group.length)
      group.forEach((piece, n) => mesh.setMatrixAt(n, piece.matrix))
      mesh.renderOrder = 10
      scene.add(mesh)
      overlay.push(mesh)
    }
  }
  return overlay
}
