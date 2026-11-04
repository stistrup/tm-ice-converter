import { Matrix3, Matrix4, Sphere, Vector3, type BufferGeometry, type Ray } from 'three'

export interface PickInstance {
  geometries: BufferGeometry[]
  matrix: Matrix4
  hidden: () => boolean
}

const origin = new Vector3()
const direction = new Vector3()
const inverse = new Matrix4()
const linear = new Matrix3()

// Möller–Trumbore against the module's origin and direction
function hitTriangle(p: ArrayLike<number>, a: number, b: number, c: number, limit: number): number {
  const e1x = p[b] - p[a]
  const e1y = p[b + 1] - p[a + 1]
  const e1z = p[b + 2] - p[a + 2]
  const e2x = p[c] - p[a]
  const e2y = p[c + 1] - p[a + 1]
  const e2z = p[c + 2] - p[a + 2]
  const hx = direction.y * e2z - direction.z * e2y
  const hy = direction.z * e2x - direction.x * e2z
  const hz = direction.x * e2y - direction.y * e2x
  const det = e1x * hx + e1y * hy + e1z * hz
  if (Math.abs(det) < 1e-9) return limit
  const f = 1 / det
  const sx = origin.x - p[a]
  const sy = origin.y - p[a + 1]
  const sz = origin.z - p[a + 2]
  const u = f * (sx * hx + sy * hy + sz * hz)
  if (u < 0 || u > 1) return limit
  const qx = sy * e1z - sz * e1y
  const qy = sz * e1x - sx * e1z
  const qz = sx * e1y - sy * e1x
  const v = f * (direction.x * qx + direction.y * qy + direction.z * qz)
  if (v < 0 || u + v > 1) return limit
  const t = f * (e2x * qx + e2y * qy + e2z * qz)
  return t > 0 && t < limit ? t : limit
}

// CPU picking: a sphere per instance, then that instance's triangles
export class Picker {
  private readonly centers: Float32Array
  private readonly radii: Float32Array

  constructor(private readonly instances: PickInstance[]) {
    this.centers = new Float32Array(instances.length * 3)
    this.radii = new Float32Array(instances.length)
    const sphere = new Sphere()
    instances.forEach((instance, i) => {
      sphere.makeEmpty()
      for (const geometry of instance.geometries) {
        if (!geometry.boundingSphere) geometry.computeBoundingSphere()
        sphere.union(geometry.boundingSphere!)
      }
      sphere.applyMatrix4(instance.matrix)
      sphere.center.toArray(this.centers, i * 3)
      this.radii[i] = sphere.radius
    })
  }

  sphereOf(index: number): Sphere {
    const c = this.centers
    return new Sphere(new Vector3(c[index * 3], c[index * 3 + 1], c[index * 3 + 2]), this.radii[index])
  }

  bounds(): Sphere {
    const sphere = new Sphere()
    sphere.makeEmpty()
    for (let i = 0; i < this.instances.length; i++) {
      if (!this.instances[i].hidden()) sphere.union(this.sphereOf(i))
    }
    return sphere
  }

  pick(ray: Ray): number | null {
    const candidates: { index: number; near: number }[] = []
    const { x: ox, y: oy, z: oz } = ray.origin
    const { x: dx, y: dy, z: dz } = ray.direction
    for (let i = 0; i < this.instances.length; i++) {
      const lx = this.centers[i * 3] - ox
      const ly = this.centers[i * 3 + 1] - oy
      const lz = this.centers[i * 3 + 2] - oz
      const along = lx * dx + ly * dy + lz * dz
      const radius = this.radii[i]
      const distance2 = lx * lx + ly * ly + lz * lz - along * along
      if (distance2 > radius * radius || along + radius < 0 || this.instances[i].hidden()) continue
      candidates.push({ index: i, near: along - Math.sqrt(radius * radius - distance2) })
    }
    candidates.sort((a, b) => a.near - b.near)

    let best = Infinity
    let hit: number | null = null
    for (const { index, near } of candidates) {
      if (near > best) break
      const t = this.hitInstance(this.instances[index], ray, best)
      if (t < best) {
        best = t
        hit = index
      }
    }
    return hit
  }

  private hitInstance(instance: PickInstance, ray: Ray, limit: number): number {
    inverse.copy(instance.matrix).invert()
    origin.copy(ray.origin).applyMatrix4(inverse)
    // not normalized: t stays in world units
    direction.copy(ray.direction).applyMatrix3(linear.setFromMatrix4(inverse))
    let best = limit
    for (const geometry of instance.geometries) {
      const positions = geometry.attributes.position.array
      const indices = geometry.index!.array
      for (let i = 0; i < indices.length; i += 3) {
        best = hitTriangle(positions, indices[i] * 3, indices[i + 1] * 3, indices[i + 2] * 3, best)
      }
    }
    return best
  }
}
