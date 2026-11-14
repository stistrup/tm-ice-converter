import {
  Box3,
  BoxGeometry,
  Color,
  ConeGeometry,
  Fog,
  InstancedMesh,
  Matrix4,
  Mesh,
  MOUSE,
  PerspectiveCamera,
  Plane,
  Raycaster,
  Scene,
  Vector2,
  Vector3,
  WebGLRenderer,
  type BufferGeometry,
  type Material,
} from 'three'
import { OrbitControls } from 'three/addons/controls/OrbitControls.js'
import type { InstanceInfo, SceneManifest, SceneMaterial } from '../api'
import { baseGround, CELL } from './baseGround'
import { categoryOf, type Category } from './categories'
import { Looks, type LookChange } from './looks'
import { flatMaterial, groundHeight, SceneMaterials } from './materials'
import { isWaterZone } from './materialRules'
import { ghostMaterial, highlightMaterial, replaceOverlay } from './overlays'
import { pendingMaterial } from './pending'
import { Picker } from './picker'
import { PivotOrbit } from './pivotOrbit'
import { originOf, ownsGroundOf, pieceGeometry, tintOf, type LayerPiece, type PieceParts, type Placed } from './placed'
import { buildLayer, type PreviewLayer } from './previewLayer'
import { drawOver, isTerrainShaped, keepUncut, markCutter, TerrainCut, type Extent } from './terrainCut'

export type { LookChange } from './looks'
export type { PieceParts } from './placed'

export interface PreviewDraw {
  changed: { manifest: SceneManifest; geometry: ArrayBuffer } | null
  looks: LookChange[]
  tints: { color: number; indices: number[] }[]
  // pieces the next result is on its way for: their converted parts are hidden
  waiting: number[]
}

const SKY = 0x393939

export interface MapSceneEvents {
  hover: (index: number | null) => void
  select: (index: number | null, add: boolean) => void
  // shift-drag: every piece touched goes in (add) or out
  paint: (index: number, add: boolean) => void
  stroke: (phase: 'start' | 'end') => void
  isSelected: (index: number) => boolean
  textureLoaded: () => void
  cameraMoved: () => void
}

export interface CameraState {
  position: [number, number, number]
  target: [number, number, number]
}

// plain three.js, nothing in here is reactive
export class MapScene {
  private readonly renderer: WebGLRenderer
  private readonly scene = new Scene()
  private readonly camera = new PerspectiveCamera(60, 1, 1, 12000)
  private readonly controls: OrbitControls
  private readonly orbit: PivotOrbit
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
  private readonly cut: TerrainCut
  private readonly looks: Looks
  private materials: SceneMaterials | null = null
  private picker: Picker | null = null
  private hoverOverlay: InstancedMesh[] = []
  private selectOverlay: InstancedMesh[] = []
  private tintOverlay: InstancedMesh[] = []
  private readonly tintMaterials = new Map<number, Material>()
  private readonly lookMaterials = new Map<string, Material>()
  private layer: PreviewLayer | null = null
  // what the layer was built from
  private layerSource: PreviewDraw['changed'] = null
  private selection: number[] = []
  private extent: Extent | null = null
  private ground: Mesh | null = null
  private environment: string | null = null
  private baseZone: string | null = null
  private previewCall = 0
  private frame = 0
  private needsRender = true
  private lastMove: { x: number; y: number } | null = null
  private pointerDown: { x: number; y: number } | null = null
  private painting: { add: boolean; touched: Set<number> } | null = null
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
    this.cut = new TerrainCut(this.renderer, this.scene, (map, box) => {
      this.materials?.setCut(map, box)
      this.layer?.materials.setCut(map, box)
      this.invalidate()
    })
    this.looks = new Looks(this.scene, this.geometries)
    this.controls = new OrbitControls(this.camera, canvas)
    // right-drag is PivotOrbit's
    this.controls.mouseButtons = { LEFT: MOUSE.PAN, MIDDLE: MOUSE.DOLLY, RIGHT: null }
    // a drag pans about the distance to the orbit point: closer than this it crawls (a block is 32 m)
    this.controls.minDistance = 16
    this.controls.zoomToCursor = true
    this.controls.screenSpacePanning = false
    this.controls.maxPolarAngle = Math.PI * 0.98
    this.controls.addEventListener('change', this.invalidate)
    this.controls.addEventListener('change', () => this.events.cameraMoved())
    this.orbit = new PivotOrbit(this.camera, this.controls, canvas, this.surfaceAt)

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
        geometry: this.own(pieceGeometry(buffer, piece.offset, piece.vertices, piece.indices, piece.colors)),
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
    this.baseZone = manifest.baseZone
    this.addGround(manifest.environment)
    this.scheduleCut()
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

  private scheduleCut() {
    if (this.extent && this.materials) this.cut.schedule(this.extent)
  }

  // the conversion preview in one step: the converted pieces (a layer of their own, built and textured first),
  // the originals they replace hidden, the rest repainted or coloured
  async showPreview(draw: PreviewDraw | null) {
    const call = ++this.previewCall
    // the same converted pieces as shown (spinners going on or off): no rebuild, so it shows at once
    const same = !!draw?.changed && draw.changed === this.layerSource && !!this.layer
    const layer = same ? this.layer : draw?.changed ? await buildLayer(draw.changed, this.scene, this.renderer, this.materials, this.invalidate) : null
    if (call !== this.previewCall) {
      if (!same) layer?.dispose()
      return
    }
    if (!same) {
      this.layer?.dispose()
      this.layer = layer
      this.layerSource = draw?.changed ?? null
      for (const mesh of layer?.meshes ?? []) this.scene.add(mesh)
    }
    layer?.hide(new Set((draw?.waiting ?? []).map((i) => this.placed[i]?.origin).filter((o): o is string => !!o)))
    this.looks.set(draw?.looks ?? [], this.placed)
    this.animating = (draw?.looks ?? []).some((look) => look.materials.includes(this.pendingMaterial))
    this.setTints(draw?.tints ?? [])
    const gone = (draw?.looks ?? []).filter((look) => !look.topsOnly && look.materials.every((m) => m === null)).map((look) => look.index)
    this.addGround(this.environment, layer?.covered ?? [], new Set(gone))
    this.setSelection(this.selection)
    this.scheduleCut()
    this.invalidate()
  }

  materialNames(index: number): (string | null)[] {
    return this.placed[index]?.materials.map((material) => this.materials?.nameOf(material) ?? null) ?? []
  }

  // a removed piece: a faint white ghost of itself
  readonly ghostMaterial: Material = ghostMaterial()
  // its item still being built: turns while shown
  readonly pendingMaterial = pendingMaterial()
  private animating = false

  // a preview material, made once per name
  lookMaterial(source: SceneMaterial): Material | null {
    if (!this.materials) return null
    let material = this.lookMaterials.get(source.name)
    if (!material) this.lookMaterials.set(source.name, (material = this.materials.extraMaterial(source)))
    return material
  }

  // the conversion preview: one see-through colour per group
  private setTints(groups: { color: number; indices: number[] }[]) {
    for (const mesh of this.tintOverlay) {
      this.scene.remove(mesh)
      mesh.dispose()
    }
    this.tintOverlay = []
    for (const { color, indices } of groups) {
      let material = this.tintMaterials.get(color)
      if (!material) this.tintMaterials.set(color, (material = highlightMaterial(color, 0.5)))
      const meshes = this.replaceOverlay([], indices, material)
      for (const mesh of meshes) mesh.renderOrder = 5
      this.tintOverlay.push(...meshes)
    }
    this.invalidate()
  }

  setSelection(indices: number[]) {
    this.selection = indices
    this.selectOverlay = this.replaceOverlay(this.selectOverlay, indices, this.selectMaterial)
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
    if (!placed) return null
    const converted = this.converted(placed)
    if (converted) return converted
    if (placed.meshKey.startsWith('placeholder:') || placed.meshKey.startsWith('box:')) return null
    return this.looks.of(index) ?? placed
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
      cut: this.cutAt(point.x, point.z),
    }
  }

  cutAt(x: number, z: number): number[] | null {
    return this.cut.at(x, z)
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
    this.orbit.dispose()
    this.controls.dispose()
    this.materials?.dispose()
    this.previewCall++
    this.cut.dispose()
    this.layer?.dispose()
    for (const geometry of this.geometries) geometry.dispose()
    for (const material of this.ownMaterials) material.dispose()
    this.hoverMaterial.dispose()
    this.selectMaterial.dispose()
    this.pendingMaterial.dispose()
    for (const material of this.tintMaterials.values()) material.dispose()
    this.renderer.dispose()
    this.renderer.forceContextLoss()
  }

  private place(meshKey: string, geometries: BufferGeometry[], materials: Material[], m: number[], info: InstanceInfo) {
    // M is row-major with world = local * M, which is three's column-major layout
    const ownsGround = ownsGroundOf(info)
    const tint = typeof info.color === 'number' ? info.color : 0
    const stacked = info.stacked === true
    const category = categoryOf(info)
    const cuts = info.kind.includes('block') && /\bground\b/.test(info.tags ?? '') && !/\bterrain\b/.test(info.tags ?? '') && !isTerrainShaped(materials)
    const origin = originOf(info, 'block', 'item')
    this.placed.push({ meshKey, geometries, materials, matrix: new Matrix4().fromArray(m), category, ownsGround, cuts, tint, stacked, slots: [], origin })
  }

  private buildInstances() {
    const groups = new Map<string, Placed[]>()
    for (const placed of this.placed) {
      const key = `${placed.meshKey}|${placed.category}|${placed.stacked}|${placed.cuts}`
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
          mesh.setColorAt(n, tintOf(placed.tint))
          placed.slots.push({ mesh, n })
        })
        mesh.matrixAutoUpdate = false
        if (group[0].cuts) markCutter(mesh)
        if (group[0].stacked) drawOver(mesh)
        if (category !== 'terrain') keepUncut(mesh)
        this.scene.add(mesh)
        const list = this.categoryMeshes.get(category)
        if (list) list.push(mesh)
        else this.categoryMeshes.set(category, [mesh])
      })
    }
  }

  // cells without a ground or terrain block show the environment's base ground; covered: cells the preview fills,
  // hidden: originals it takes away
  private addGround(environment: string | null, covered: Iterable<string> = [], hidden: ReadonlySet<number> = new Set()) {
    this.environment = environment
    if (this.ground) {
      this.scene.remove(this.ground)
      this.ground.geometry.dispose()
      this.ground = null
    }
    const { geometry, extent } = baseGround(this.placed, environment, this.baseZone, covered, hidden)
    this.extent = extent
    if (!geometry) return
    this.ground = new Mesh(this.own(geometry), isWaterZone(this.baseZone) ? this.materials!.water() : this.materials!.ground())
    this.scene.add(this.ground)
    this.invalidate()
  }

  private own(geometry: BufferGeometry) {
    this.geometries.push(geometry)
    return geometry
  }

  private ownMaterial<T extends Material>(material: T): T {
    this.ownMaterials.push(material)
    return material
  }

  // the preview's converted piece that replaces this one, while the preview shows it
  private converted(placed: Placed): LayerPiece | null {
    return (placed.origin && this.layer?.pieces.get(placed.origin)) || null
  }
  // in the preview, converted pieces are outlined as converted
  private replaceOverlay(old: InstancedMesh[], indices: number[], material: Material): InstancedMesh[] {
    const shown = indices.flatMap((index) => {
      const placed = this.placed[index]
      if (!placed) return []
      return [this.converted(placed) ?? { key: placed.meshKey, geometries: placed.geometries, matrix: placed.matrix }]
    })
    return replaceOverlay(this.scene, old, shown, material)
  }

  // from outside the view (a list): highlighted as the pointer would
  showHover(index: number | null) {
    this.setHover(index)
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

  // nearest of the pieces, the base ground, else the ground plane
  private readonly surfaceAt = (x: number, y: number): Vector3 | null => {
    const ray = this.rayAt(x, y).clone()
    let distance = this.picker?.hit(ray)?.distance ?? Infinity
    if (this.ground) {
      const ground = this.raycaster.intersectObject(this.ground, false)[0]
      if (ground) distance = Math.min(distance, ground.distance)
    }
    if (distance < Infinity) return ray.at(distance, new Vector3())
    return ray.intersectPlane(new Plane(new Vector3(0, 1, 0), -groundHeight(this.environment)), new Vector3())
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
    if (this.lastMove && this.painting) {
      const index = this.pickAt(this.lastMove.x, this.lastMove.y)
      this.lastMove = null
      if (index !== null) this.paint(index)
    } else if (this.lastMove && !this.pointerDown) {
      this.setHover(this.pickAt(this.lastMove.x, this.lastMove.y))
      this.lastMove = null
    }
    if (this.animating) {
      this.pendingMaterial.uniforms.time.value = time / 1000
      this.needsRender = true
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
    if (event.button !== 0 || !event.shiftKey) return
    this.controls.enabled = false
    // the first piece decides: starting on a selected one unselects
    const index = this.pickAt(event.clientX, event.clientY)
    this.painting = { add: index === null || !this.events.isSelected(index), touched: new Set() }
    this.events.stroke('start')
    if (index !== null) this.paint(index)
  }

  private paint(index: number) {
    if (!this.painting || this.painting.touched.has(index)) return
    this.painting.touched.add(index)
    this.events.paint(index, this.painting.add)
  }

  private readonly onPointerUp = (event: PointerEvent) => {
    const down = this.pointerDown
    this.pointerDown = null
    if (this.painting) {
      this.painting = null
      this.events.stroke('end')
      if (!this.keys.has('ShiftLeft') && !this.keys.has('ShiftRight')) this.controls.enabled = true
      return
    }
    if (!down || event.button !== 0) return
    if (Math.hypot(event.clientX - down.x, event.clientY - down.y) > 4) return
    this.events.select(this.pickAt(event.clientX, event.clientY), event.shiftKey)
  }

  private readonly onPointerLeave = () => {
    this.lastMove = null
    this.setHover(null)
  }

  private readonly onKeyDown = (event: KeyboardEvent) => {
    if (event.target instanceof HTMLInputElement) return
    this.keys.add(event.code)
    // shift is for selecting: the mouse leaves the camera alone
    if (event.key === 'Shift') this.controls.enabled = false
  }

  private readonly onKeyUp = (event: KeyboardEvent) => {
    this.keys.delete(event.code)
    if (event.key === 'Shift') this.controls.enabled = true
    if (/^Key[WASDQE]$/.test(event.code)) this.orbit.reseat()
  }

  private readonly onBlur = () => {
    this.keys.clear()
    this.controls.enabled = true
    if (this.painting) this.events.stroke('end')
    this.painting = null
  }
}
