import { Color, Mesh, MOUSE, PerspectiveCamera, Scene, Sphere, Vector3, WebGLRenderer } from 'three'
import { OrbitControls } from 'three/addons/controls/OrbitControls.js'
import type { PieceParts } from './MapScene'

const BACKGROUND = 0x1d1d1d

// one selected piece on its own canvas; geometry and materials stay MapScene's
export class PiecePreview {
  private readonly renderer: WebGLRenderer
  private readonly scene = new Scene()
  private readonly camera = new PerspectiveCamera(30, 1, 0.1, 1000)
  private readonly controls: OrbitControls
  private readonly resize: ResizeObserver
  private frame = 0

  constructor(private readonly canvas: HTMLCanvasElement) {
    this.renderer = new WebGLRenderer({ canvas, antialias: true })
    this.renderer.setPixelRatio(window.devicePixelRatio)
    this.scene.background = new Color(BACKGROUND)
    this.controls = new OrbitControls(this.camera, canvas)
    this.controls.enablePan = false
    this.controls.mouseButtons = { LEFT: MOUSE.ROTATE, MIDDLE: MOUSE.DOLLY, RIGHT: MOUSE.ROTATE }
    this.controls.addEventListener('change', this.invalidate)
    this.resize = new ResizeObserver(() => this.fit())
    this.resize.observe(canvas)
  }

  show(parts: PieceParts | null) {
    this.scene.clear()
    if (parts) {
      const sphere = new Sphere()
      sphere.makeEmpty()
      parts.geometries.forEach((geometry, i) => {
        if (!geometry.boundingSphere) geometry.computeBoundingSphere()
        sphere.union(geometry.boundingSphere!)
        this.scene.add(new Mesh(geometry, parts.materials[i]))
      })
      const radius = Math.max(sphere.radius, 0.1)
      const distance = (radius / Math.sin((this.camera.fov / 2) * (Math.PI / 180))) * 1.05
      this.camera.near = distance / 100
      this.camera.far = distance + radius * 4
      this.camera.updateProjectionMatrix()
      this.controls.target.copy(sphere.center)
      this.controls.minDistance = radius * 0.3
      this.controls.maxDistance = distance * 3
      this.camera.position.copy(sphere.center).add(new Vector3(0.7, 0.55, 0.8).setLength(distance))
      this.controls.update()
    }
    this.invalidate()
  }

  readonly invalidate = () => {
    cancelAnimationFrame(this.frame)
    this.frame = requestAnimationFrame(() => {
      if (this.canvas.clientWidth > 0) this.renderer.render(this.scene, this.camera)
    })
  }

  dispose() {
    cancelAnimationFrame(this.frame)
    this.resize.disconnect()
    this.controls.dispose()
    this.scene.clear()
    this.renderer.dispose()
    this.renderer.forceContextLoss()
  }

  private fit() {
    const { clientWidth: width, clientHeight: height } = this.canvas
    if (width === 0 || height === 0) return
    this.renderer.setSize(width, height, false)
    this.camera.aspect = width / height
    this.camera.updateProjectionMatrix()
    this.invalidate()
  }
}
