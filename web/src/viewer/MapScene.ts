import {
  Box3,
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
  MOUSE,
  PerspectiveCamera,
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
import { flatMaterial, groundHeight, SceneMaterials } from './materials'
import { Picker } from './picker'

interface Placed {
  meshKey: string
  geometries: BufferGeometry[]
  materials: Material[]
  matrix: Matrix4
  category: Category
  // ground and terrain blocks draw their own ground; the base ground shows elsewhere
  ownsGround: boolean
  // block colour: 0 Default (as painted), 1 White .. 5 Black
  tint: number
}

const CELL = { x: 32, y: 8, z: 32 }
const tintColor = new Color()
const SKY = 0x393939

export interface MapSceneEvents {
  hover: (index: number | null) => void
  select: (index: number | null) => void
  textureLoaded: () => void
  cameraMoved: () => void
}

export interface CameraState {
  position: [number, number, number]
  target: [number, number, number]
}

export interface PieceParts {
  geometries: BufferGeometry[]
  materials: Material[]
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
  private readonly selectMaterial = highlightMaterial(0xe5912f, 0.6)
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
    this.controls.mouseButtons = { LEFT: MOUSE.PAN, MIDDLE: MOUSE.DOLLY, RIGHT: MOUSE.ROTATE }
    // a drag pans about the distance to the orbit point: closer than this it crawls (a block is 32 m)
    this.controls.minDistance = 16
    this.controls.zoomToCursor = true
    this.controls.screenSpacePanning = false
    this.controls.maxPolarAngle = Math.PI * 0.98
    this.controls.addEventListener('change', this.invalidate)
    this.controls.addEventListener('change', () => this.events.cameraMoved())

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
    this.materials = new SceneMaterials(manifest.materials, this.renderer, () => {
      this.invalidate()
      this.events.textureLoaded()
    }, manifest.environment)
    const pieces = manifest.meshes.map((mesh) =>
      mesh.pieces.map((piece) => ({
        geometry: this.pieceGeometry(buffer, piece.offset, piece.vertices, piece.indices, piece.colors),
        material: this.materials!.materials[piece.material] as Material,
      })),
    )

    for (const instance of manifest.instances) {
      const mesh = pieces[instance.mesh]
      this.place(manifest.meshes[instance.mesh].key, mesh.map((p) => p.geometry), mesh.map((p) => p.material), instance.m, instance.info)
    }
    const marker = this.own(new ConeGeometry(3.5, 10, 10).translate(0, 8, 0))
    const markerMaterial = this.ownMaterial(flatMaterial([0.85, 0.9, 0.95]))
    const treeMaterial = this.ownMaterial(flatMaterial([0.2, 0.42, 0.18]))
    for (const placeholder of manifest.placeholders) {
      const material = placeholder.info.missing === 'VegetTreeModel' ? treeMaterial : markerMaterial
      this.place(`placeholder:${placeholder.mesh}`, [marker], [material], placeholder.m, { ...placeholder.info, placeholder: true })
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
    this.addGround(manifest.environment)
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

  setSelection(index: number | null) {
    this.selectOverlay = this.replaceOverlay(this.selectOverlay, index === null ? [] : [index], this.selectMaterial)
    this.invalidate()
  }

  focus(index: number) {
    if (!this.picker) return
    const { center, radius } = this.picker.sphereOf(index)
    const offset = this.camera.position.clone().sub(this.controls.target)
    offset.setLength(Math.max(radius * 2.6, 40))
    this.controls.target.copy(center)
    this.camera.position.copy(center).add(offset)
    this.controls.update()
    this.invalidate()
  }

  // null without a real mesh: boxes and cones stand in for those
  partsOf(index: number): PieceParts | null {
    const placed = this.placed[index]
    if (!placed || placed.meshKey.startsWith('placeholder:') || placed.meshKey.startsWith('box:')) return null
    return placed
  }

  sizeOf(index: number): Vector3 | null {
    const placed = this.placed[index]
    if (!placed) return null
    const box = new Box3()
    for (const geometry of placed.geometries) {
      if (!geometry.boundingBox) geometry.computeBoundingBox()
      box.union(geometry.boundingBox!)
    }
    return box.getSize(new Vector3())
  }

  cameraState(): CameraState {
    const round = (v: Vector3) => [v.x, v.y, v.z].map((n) => Math.round(n * 10) / 10) as [number, number, number]
    return { position: round(this.camera.position), target: round(this.controls.target) }
  }

  setCamera({ position, target }: CameraState) {
    this.camera.position.set(...position)
    this.controls.target.set(...target)
    this.controls.update()
    this.invalidate()
  }

  positionOf(index: number): Vector3 | null {
    const placed = this.placed[index]
    return placed ? new Vector3().setFromMatrixPosition(placed.matrix) : null
  }

  // x, y: 0..1 across the view from the top left; what a click there would hit, and its material
  pickDetail(x: number, y: number) {
    if (!this.picker) return null
    const rect = this.canvas.getBoundingClientRect()
    const ray = this.rayAt(rect.left + x * rect.width, rect.top + y * rect.height)
    const hit = this.picker.hit(ray)
    if (!hit) return null
    const placed = this.placed[hit.index]
    const point = ray.at(hit.distance, new Vector3())
    return {
      index: hit.index,
      material: this.materials?.nameOf(placed.materials[hit.part]) ?? null,
      point: [point.x, point.y, point.z].map((n) => Math.round(n * 100) / 100),
      distance: Math.round(hit.distance * 10) / 10,
    }
  }

  // rendered now: the drawing buffer is only readable in the same task
  capture(): string {
    this.renderer.render(this.scene, this.camera)
    return this.canvas.toDataURL('image/png')
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
    const ownsGround = info.kind.includes('block') && /\b(ground|terrain)\b/.test(info.tags ?? '')
    const tint = typeof info.color === 'number' ? info.color : 0
    this.placed.push({ meshKey, geometries, materials, matrix: new Matrix4().fromArray(m), category: categoryOf(info), ownsGround, tint })
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
        group.forEach((placed, n) => {
          mesh.setMatrixAt(n, placed.matrix)
          // the colour index rides in the instance colour's red channel
          mesh.setColorAt(n, tintColor.setRGB(placed.tint / 8, 0, 0))
        })
        mesh.matrixAutoUpdate = false
        this.scene.add(mesh)
        const list = this.categoryMeshes.get(category)
        if (list) list.push(mesh)
        else this.categoryMeshes.set(category, [mesh])
      })
    }
  }

  // cells without a ground or terrain block show the environment's base ground
  private addGround(environment: string | null) {
    const cell = CELL.x
    const owned = new Set<string>()
    let [minX, maxX, minZ, maxZ] = [0, 48, 0, 48]
    const box = new Box3()
    const part = new Box3()
    for (const placed of this.placed) {
      box.makeEmpty()
      for (const geometry of placed.geometries) {
        if (!geometry.boundingBox) geometry.computeBoundingBox()
        box.union(part.copy(geometry.boundingBox!).applyMatrix4(placed.matrix))
      }
      if (box.isEmpty()) continue
      // a hair inside, so a block doesn't claim the cells it only touches
      const [x0, x1] = [Math.floor((box.min.x + 0.5) / cell), Math.floor((box.max.x - 0.5) / cell)]
      const [z0, z1] = [Math.floor((box.min.z + 0.5) / cell), Math.floor((box.max.z - 0.5) / cell)]
      ;[minX, maxX, minZ, maxZ] = [Math.min(minX, x0), Math.max(maxX, x1 + 1), Math.min(minZ, z0), Math.max(maxZ, z1 + 1)]
      if (!placed.ownsGround) continue
      for (let x = x0; x <= x1; x++) for (let z = z0; z <= z1; z++) owned.add(`${x},${z}`)
    }
    const positions: number[] = []
    // exact: it only fills cells no block owns, so nothing shares its plane
    const y = groundHeight(environment)
    for (let x = minX; x < maxX; x++) {
      for (let z = minZ; z < maxZ; z++) {
        if (owned.has(`${x},${z}`)) continue
        const [ax, bx, az, bz] = [x * cell, (x + 1) * cell, z * cell, (z + 1) * cell]
        positions.push(ax, y, az, ax, y, bz, bx, y, bz, ax, y, az, bx, y, bz, bx, y, az)
      }
    }
    if (positions.length === 0) return
    const geometry = this.own(new BufferGeometry())
    geometry.setAttribute('position', new BufferAttribute(new Float32Array(positions), 3))
    geometry.setAttribute('normal', new BufferAttribute(new Float32Array(positions.length).map((_, i) => (i % 3 === 1 ? 1 : 0)), 3))
    geometry.setAttribute('uv', new BufferAttribute(new Float32Array((positions.length / 3) * 2), 2))
    const ground = new Mesh(geometry, this.materials!.ground())
    this.scene.add(ground)
  }

  private pieceGeometry(buffer: ArrayBuffer, offset: number, vertices: number, indices: number, colors: boolean) {
    const geometry = new BufferGeometry()
    geometry.setAttribute('position', new BufferAttribute(new Float32Array(buffer, offset, vertices * 3), 3))
    geometry.setAttribute('normal', new BufferAttribute(new Float32Array(buffer, offset + vertices * 12, vertices * 3), 3))
    geometry.setAttribute('uv', new BufferAttribute(new Float32Array(buffer, offset + vertices * 24, vertices * 2), 2))
    geometry.setIndex(new BufferAttribute(new Uint32Array(buffer, offset + vertices * 32, indices), 1))
    if (colors) geometry.setAttribute('cv', new BufferAttribute(new Float32Array(buffer, offset + vertices * 32 + indices * 4, vertices * 4), 4))
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

  private rayAt(x: number, y: number) {
    const rect = this.canvas.getBoundingClientRect()
    const ndc = new Vector2(((x - rect.left) / rect.width) * 2 - 1, -((y - rect.top) / rect.height) * 2 + 1)
    this.raycaster.setFromCamera(ndc, this.camera)
    return this.raycaster.ray
  }

  private pickAt(x: number, y: number): number | null {
    if (!this.picker) return null
    return this.picker.pick(this.rayAt(x, y))
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
