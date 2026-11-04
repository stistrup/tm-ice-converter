import {
  BoxGeometry,
  BufferAttribute,
  BufferGeometry,
  Color,
  ConeGeometry,
  DoubleSide,
  Fog,
  InstancedMesh,
  Matrix4,
  Mesh,
  MeshBasicMaterial,
  PerspectiveCamera,
  PlaneGeometry,
  Raycaster,
  Scene,
  Vector2,
  Vector3,
  WebGLRenderer,
  type Material,
} from 'three'
import { OrbitControls } from 'three/addons/controls/OrbitControls.js'
import type { InstanceInfo, SceneManifest } from '../api'
import { categoryOf, type Category } from './categories'
import { flatMaterial, SceneMaterials } from './materials'
import { Picker } from './picker'

interface Placed {
  meshKey: string
  geometries: BufferGeometry[]
  materials: Material[]
  matrix: Matrix4
  category: Category
}

const CELL = { x: 32, y: 8, z: 32 }
const SKY = 0x101820

export interface MapSceneEvents {
  hover: (index: number | null) => void
  select: (index: number | null) => void
}

// plain three.js, nothing in here is reactive
export class MapScene {
  private readonly renderer: WebGLRenderer
  private readonly scene = new Scene()
  private readonly camera = new PerspectiveCamera(60, 1, 1, 12000)
  private readonly controls: OrbitControls
  private readonly raycaster = new Raycaster()
  private readonly placed: Placed[] = []
  private readonly categoryMeshes = new Map<Category, InstancedMesh[]>()
  private readonly hiddenCategories = new Set<Category>()
  private readonly geometries: BufferGeometry[] = []
  private readonly ownMaterials: Material[] = []
  private readonly resize: ResizeObserver
  private readonly keys = new Set<string>()
  private readonly hoverMaterial = highlightMaterial(0xffffff, 0.3)
  private readonly selectMaterial = highlightMaterial(0x7cc8f0, 0.55)
  private materials: SceneMaterials | null = null
  private picker: Picker | null = null
  private hoverOverlay: InstancedMesh[] = []
  private selectOverlay: InstancedMesh[] = []
  private frame = 0
  private needsRender = true
  private lastMove: { x: number; y: number } | null = null
  private pointerDown: { x: number; y: number } | null = null
  private hoverIndex: number | null = null
  private lastTime = 0

  constructor(
    private readonly canvas: HTMLCanvasElement,
    private readonly events: MapSceneEvents,
  ) {
    this.renderer = new WebGLRenderer({ canvas, antialias: true })
    this.renderer.setPixelRatio(window.devicePixelRatio)
    this.scene.background = new Color(SKY)
    this.scene.fog = new Fog(SKY, 4000, 12000)
    this.controls = new OrbitControls(this.camera, canvas)
    this.controls.zoomToCursor = true
    this.controls.screenSpacePanning = false
    this.controls.maxPolarAngle = Math.PI * 0.98
    this.controls.addEventListener('change', this.invalidate)

    this.resize = new ResizeObserver(() => this.fit())
    this.resize.observe(canvas)
    canvas.addEventListener('pointermove', this.onPointerMove)
    canvas.addEventListener('pointerdown', this.onPointerDown)
    canvas.addEventListener('pointerup', this.onPointerUp)
    canvas.addEventListener('pointerleave', this.onPointerLeave)
    window.addEventListener('keydown', this.onKeyDown)
    window.addEventListener('keyup', this.onKeyUp)
    window.addEventListener('blur', this.onBlur)
    this.frame = requestAnimationFrame(this.tick)
  }

  load(manifest: SceneManifest, buffer: ArrayBuffer) {
    this.materials = new SceneMaterials(manifest.materials, this.renderer, this.invalidate)
    const pieces = manifest.meshes.map((mesh) =>
      mesh.pieces.map((piece) => ({
        geometry: this.pieceGeometry(buffer, piece.offset, piece.vertices, piece.indices),
        material: this.materials!.materials[piece.material] as Material,
      })),
    )

    for (const instance of manifest.instances) {
      const mesh = pieces[instance.mesh]
      this.place(manifest.meshes[instance.mesh].key, mesh.map((p) => p.geometry), mesh.map((p) => p.material), instance.m, instance.info)
    }
    const marker = this.own(new ConeGeometry(3.5, 10, 10).translate(0, 8, 0))
    const markerMaterial = this.ownMaterial(flatMaterial([0.85, 0.9, 0.95]))
    for (const placeholder of manifest.placeholders) {
      this.place(`placeholder:${placeholder.mesh}`, [marker], [markerMaterial], placeholder.m, { ...placeholder.info, placeholder: true })
    }
    const boxes = new Map<string, BufferGeometry>()
    const boxMaterial = this.ownMaterial(flatMaterial([0.9, 0.1, 0.8]))
    for (const box of manifest.boxes) {
      const [x, y, z] = box.info.units as [number, number, number]
      const key = `${x}|${y}|${z}`
      if (!boxes.has(key)) {
        boxes.set(key, this.own(new BoxGeometry(x * CELL.x, y * CELL.y, z * CELL.z).translate((x * CELL.x) / 2, (y * CELL.y) / 2, (z * CELL.z) / 2)))
      }
      this.place(`box:${box.mesh}|${key}`, [boxes.get(key)!], [boxMaterial], box.m, box.info)
    }

    this.buildInstances()
    if ((manifest.environment ?? 'Stadium').startsWith('Stadium')) this.addGround()
    this.picker = new Picker(
      this.placed.map((placed) => ({
        geometries: placed.geometries,
        matrix: placed.matrix,
        hidden: () => this.hiddenCategories.has(placed.category),
      })),
    )
    this.frameAll()
    void this.materials.loadTextures()
    this.invalidate()
  }

  setHiddenCategories(hidden: Category[]) {
    this.hiddenCategories.clear()
    for (const category of hidden) this.hiddenCategories.add(category)
    for (const [category, meshes] of this.categoryMeshes) {
      for (const mesh of meshes) mesh.visible = !this.hiddenCategories.has(category)
    }
    this.setHover(null)
    this.invalidate()
  }

  setSelection(indices: number[]) {
    this.selectOverlay = this.replaceOverlay(this.selectOverlay, indices, this.selectMaterial)
    this.invalidate()
  }

  focus(indices: number[]) {
    if (!this.picker || indices.length === 0) return
    const sphere = this.picker.sphereOf(indices[0]).clone()
    for (const index of indices) sphere.union(this.picker.sphereOf(index))
    const offset = this.camera.position.clone().sub(this.controls.target)
    offset.setLength(Math.max(sphere.radius * 2.6, 40))
    this.controls.target.copy(sphere.center)
    this.camera.position.copy(sphere.center).add(offset)
    this.controls.update()
    this.invalidate()
  }

  frameAll() {
    if (!this.picker) return
    const { center, radius } = this.picker.bounds()
    this.controls.target.copy(center)
    this.camera.position.copy(center).add(new Vector3(0.6, 0.65, 0.8).setLength(Math.max(radius * 1.4, 100)))
    this.controls.update()
    this.invalidate()
  }

  dispose() {
    cancelAnimationFrame(this.frame)
    this.resize.disconnect()
    this.canvas.removeEventListener('pointermove', this.onPointerMove)
    this.canvas.removeEventListener('pointerdown', this.onPointerDown)
    this.canvas.removeEventListener('pointerup', this.onPointerUp)
    this.canvas.removeEventListener('pointerleave', this.onPointerLeave)
    window.removeEventListener('keydown', this.onKeyDown)
    window.removeEventListener('keyup', this.onKeyUp)
    window.removeEventListener('blur', this.onBlur)
    this.controls.dispose()
    this.materials?.dispose()
    for (const geometry of this.geometries) geometry.dispose()
    for (const material of this.ownMaterials) material.dispose()
    this.hoverMaterial.dispose()
    this.selectMaterial.dispose()
    this.renderer.dispose()
    this.renderer.forceContextLoss()
  }

  private place(meshKey: string, geometries: BufferGeometry[], materials: Material[], m: number[], info: InstanceInfo) {
    // M is row-major with world = local * M, which is three's column-major layout
    this.placed.push({ meshKey, geometries, materials, matrix: new Matrix4().fromArray(m), category: categoryOf(info) })
  }

  private buildInstances() {
    const groups = new Map<string, Placed[]>()
    for (const placed of this.placed) {
      const key = `${placed.meshKey}|${placed.category}`
      const group = groups.get(key)
      if (group) group.push(placed)
      else groups.set(key, [placed])
    }
    for (const group of groups.values()) {
      const { geometries, materials, category } = group[0]
      geometries.forEach((geometry, i) => {
        const mesh = new InstancedMesh(geometry, materials[i], group.length)
        group.forEach((placed, n) => mesh.setMatrixAt(n, placed.matrix))
        mesh.matrixAutoUpdate = false
        this.scene.add(mesh)
        const list = this.categoryMeshes.get(category)
        if (list) list.push(mesh)
        else this.categoryMeshes.set(category, [mesh])
      })
    }
  }

  private addGround() {
    const size = 48 * 32
    const geometry = this.own(new PlaneGeometry(size, size).rotateX(-Math.PI / 2))
    const ground = new Mesh(geometry, this.ownMaterial(flatMaterial([0.16, 0.3, 0.09])))
    ground.position.set(size / 2, 8 - 0.05, size / 2)
    this.scene.add(ground)
  }

  private pieceGeometry(buffer: ArrayBuffer, offset: number, vertices: number, indices: number) {
    const geometry = new BufferGeometry()
    geometry.setAttribute('position', new BufferAttribute(new Float32Array(buffer, offset, vertices * 3), 3))
    geometry.setAttribute('normal', new BufferAttribute(new Float32Array(buffer, offset + vertices * 12, vertices * 3), 3))
    geometry.setAttribute('uv', new BufferAttribute(new Float32Array(buffer, offset + vertices * 24, vertices * 2), 2))
    geometry.setIndex(new BufferAttribute(new Uint32Array(buffer, offset + vertices * 32, indices), 1))
    return this.own(geometry)
  }

  private own(geometry: BufferGeometry) {
    this.geometries.push(geometry)
    return geometry
  }

  private ownMaterial<T extends Material>(material: T): T {
    this.ownMaterials.push(material)
    return material
  }

  private replaceOverlay(old: InstancedMesh[], indices: number[], material: Material): InstancedMesh[] {
    for (const mesh of old) {
      this.scene.remove(mesh)
      mesh.dispose()
    }
    const groups = new Map<string, Placed[]>()
    for (const index of indices) {
      const placed = this.placed[index]
      if (!placed) continue
      const group = groups.get(placed.meshKey)
      if (group) group.push(placed)
      else groups.set(placed.meshKey, [placed])
    }
    const overlay: InstancedMesh[] = []
    for (const group of groups.values()) {
      for (const geometry of group[0].geometries) {
        const mesh = new InstancedMesh(geometry, material, group.length)
        group.forEach((placed, n) => mesh.setMatrixAt(n, placed.matrix))
        mesh.renderOrder = 10
        this.scene.add(mesh)
        overlay.push(mesh)
      }
    }
    return overlay
  }

  private setHover(index: number | null) {
    if (index === this.hoverIndex) return
    this.hoverIndex = index
    this.hoverOverlay = this.replaceOverlay(this.hoverOverlay, index === null ? [] : [index], this.hoverMaterial)
    this.events.hover(index)
    this.invalidate()
  }

  private pickAt(x: number, y: number): number | null {
    if (!this.picker) return null
    const rect = this.canvas.getBoundingClientRect()
    const ndc = new Vector2(((x - rect.left) / rect.width) * 2 - 1, -((y - rect.top) / rect.height) * 2 + 1)
    this.raycaster.setFromCamera(ndc, this.camera)
    return this.picker.pick(this.raycaster.ray)
  }

  private fit() {
    const { clientWidth: width, clientHeight: height } = this.canvas
    if (width === 0 || height === 0) return
    this.renderer.setSize(width, height, false)
    this.camera.aspect = width / height
    this.camera.updateProjectionMatrix()
    this.invalidate()
  }

  private moveWithKeys(seconds: number) {
    const forward = (this.keys.has('KeyW') ? 1 : 0) - (this.keys.has('KeyS') ? 1 : 0)
    const right = (this.keys.has('KeyD') ? 1 : 0) - (this.keys.has('KeyA') ? 1 : 0)
    const up = (this.keys.has('KeyE') ? 1 : 0) - (this.keys.has('KeyQ') ? 1 : 0)
    if (!forward && !right && !up) return
    const fast = this.keys.has('ShiftLeft') || this.keys.has('ShiftRight')
    const speed = (fast ? 3 : 1) * Math.max(this.camera.position.distanceTo(this.controls.target), 60) * seconds
    const look = new Vector3()
    this.camera.getWorldDirection(look)
    const flat = new Vector3(look.x, 0, look.z).normalize()
    const side = new Vector3().crossVectors(flat, this.camera.up).normalize()
    const move = flat.multiplyScalar(forward).add(side.multiplyScalar(right)).add(new Vector3(0, up, 0)).multiplyScalar(speed)
    this.camera.position.add(move)
    this.controls.target.add(move)
    this.controls.update()
    this.invalidate()
  }

  private readonly invalidate = () => {
    this.needsRender = true
  }

  private readonly tick = (time: number) => {
    this.frame = requestAnimationFrame(this.tick)
    this.moveWithKeys(Math.min((time - this.lastTime) / 1000, 0.1))
    this.lastTime = time
    if (this.lastMove && !this.pointerDown) {
      this.setHover(this.pickAt(this.lastMove.x, this.lastMove.y))
      this.lastMove = null
    }
    if (this.needsRender) {
      this.needsRender = false
      this.renderer.render(this.scene, this.camera)
    }
  }

  private readonly onPointerMove = (event: PointerEvent) => {
    this.lastMove = { x: event.clientX, y: event.clientY }
  }

  private readonly onPointerDown = (event: PointerEvent) => {
    this.pointerDown = { x: event.clientX, y: event.clientY }
    this.setHover(null)
  }

  private readonly onPointerUp = (event: PointerEvent) => {
    const down = this.pointerDown
    this.pointerDown = null
    if (!down || event.button !== 0) return
    if (Math.hypot(event.clientX - down.x, event.clientY - down.y) > 4) return
    this.events.select(this.pickAt(event.clientX, event.clientY))
  }

  private readonly onPointerLeave = () => {
    this.lastMove = null
    this.setHover(null)
  }

  private readonly onKeyDown = (event: KeyboardEvent) => {
    if (event.target instanceof HTMLInputElement) return
    this.keys.add(event.code)
  }

  private readonly onKeyUp = (event: KeyboardEvent) => {
    this.keys.delete(event.code)
  }

  private readonly onBlur = () => {
    this.keys.clear()
  }
}

function highlightMaterial(color: number, opacity: number) {
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
