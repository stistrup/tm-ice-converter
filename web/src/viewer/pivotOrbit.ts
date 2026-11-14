import { Quaternion, Vector3, type PerspectiveCamera } from 'three'
import type { OrbitControls } from 'three/addons/controls/OrbitControls.js'

// right-drag orbits the surface point under the mouse; the controls' target stays on the view's centre ray
export class PivotOrbit {
  private pivot: Vector3 | null = null
  private last = { x: 0, y: 0 }

  constructor(
    private readonly camera: PerspectiveCamera,
    private readonly controls: OrbitControls,
    private readonly canvas: HTMLCanvasElement,
    // world point under a screen position, null for nothing
    private readonly surfaceAt: (x: number, y: number) => Vector3 | null,
  ) {
    canvas.addEventListener('pointerdown', this.onDown)
    canvas.addEventListener('pointermove', this.onMove)
    canvas.addEventListener('pointerup', this.onUp)
    // after a zoom or pan: the target back on the surface at the centre, so zoom and pan speeds follow real distances
    controls.addEventListener('end', this.reseat)
  }

  reseat = () => {
    const rect = this.canvas.getBoundingClientRect()
    const hit = this.surfaceAt(rect.left + rect.width / 2, rect.top + rect.height / 2)
    if (!hit) return
    const distance = hit.distanceTo(this.camera.position)
    if (distance < this.controls.minDistance) return
    this.controls.target.copy(this.camera.position).add(this.forward().multiplyScalar(distance))
    this.controls.update()
  }

  private forward() {
    return this.camera.getWorldDirection(new Vector3())
  }

  private readonly onDown = (event: PointerEvent) => {
    if (event.button !== 2 || !this.controls.enabled) return
    this.pivot = this.surfaceAt(event.clientX, event.clientY) ?? this.controls.target.clone()
    this.last = { x: event.clientX, y: event.clientY }
  }

  private readonly onMove = (event: PointerEvent) => {
    if (!this.pivot || !this.controls.enabled) return
    const height = this.canvas.clientHeight || 1
    const yaw = (-2 * Math.PI * (event.clientX - this.last.x)) / height
    let pitch = (-2 * Math.PI * (event.clientY - this.last.y)) / height
    this.last = { x: event.clientX, y: event.clientY }
    // straight down to almost straight up, as the controls allow
    const now = Math.asin(Math.max(-1, Math.min(1, this.forward().y)))
    pitch = Math.max(-Math.PI / 2 + 0.01, Math.min(Math.PI / 2 - 0.07, now + pitch)) - now
    const right = new Vector3(1, 0, 0).applyQuaternion(this.camera.quaternion)
    const turn = new Quaternion()
      .setFromAxisAngle(new Vector3(0, 1, 0), yaw)
      .multiply(new Quaternion().setFromAxisAngle(right, pitch))
    const around = (point: Vector3) => point.sub(this.pivot!).applyQuaternion(turn).add(this.pivot!)
    around(this.camera.position)
    around(this.controls.target)
    this.camera.quaternion.premultiply(turn)
    this.controls.update()
  }

  private readonly onUp = (event: PointerEvent) => {
    if (event.button !== 2 || !this.pivot) return
    this.pivot = null
    this.reseat()
  }

  dispose() {
    this.canvas.removeEventListener('pointerdown', this.onDown)
    this.canvas.removeEventListener('pointermove', this.onMove)
    this.canvas.removeEventListener('pointerup', this.onUp)
    this.controls.removeEventListener('end', this.reseat)
  }
}
