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
  FloatType,
  HalfFloatType,
  LinearFilter,
  RGFormat,
  OrthographicCamera,
  ShaderMaterial,
  Vector4,
  WebGLRenderTarget,
  PerspectiveCamera,
  Raycaster,
  Scene,
  Vector2,
  Vector3,
  WebGLRenderer,
  type Material,
} from 'three'
import { OrbitControls } from 'three/addons/controls/OrbitControls.js'
import type { InstanceInfo, SceneManifest, SceneMaterial } from '../api'
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
  // ground blocks cut the terrain around them, custom ones too (they don't own the ground, the game still reshapes it)
  cuts: boolean
  // ice stacked 1 cm over the same native block: too close for the depth buffer, drawn in front
  stacked: boolean
  // where it sits in the instanced meshes, one per geometry
  slots: { mesh: InstancedMesh; n: number }[]
  // 'b3' / 'i7': block or item index in the map, what the preview's converted pieces point back to
  origin: string | null
}

// a converted piece in the preview layer, by the original it replaces
interface LayerPiece extends PieceParts {
  key: string
  matrix: Matrix4
}

function originOf(info: InstanceInfo, block: string, item: string): string | null {
  return typeof info[block] === 'number' ? `b${info[block]}` : typeof info[item] === 'number' ? `i${info[item]}` : null
}

// the preview's redraw of one piece: per geometry a material (null = left out), on the whole piece or only on its upward faces
export interface LookChange {
  index: number
  // undefined = as it is (whole piece) / no ice (tops only)
  materials: (Material | null | undefined)[]
  topsOnly: boolean
}

export interface PreviewDraw {
  changed: { manifest: SceneManifest; geometry: ArrayBuffer } | null
  looks: LookChange[]
  tints: { color: number; indices: number[] }[]
}

const hidden = new Matrix4().makeScale(0, 0, 0)

const CUT_LAYER = 1

// ground blocks (not terrain) are what the terrain gets cut around
// polygon offset only while this mesh draws: the material stays shared
function drawOver(mesh: InstancedMesh) {
  mesh.onBeforeRender = (_r, _s, _c, _g, material) => {
    material.polygonOffset = true
    material.polygonOffsetFactor = -1
    material.polygonOffsetUnits = -4
  }
  mesh.onAfterRender = (_r, _s, _c, _g, material) => {
    material.polygonOffset = false
  }
}

function markCutter(mesh: InstancedMesh) {
  const material = mesh.material as ShaderMaterial
  if (material.uniforms?.terrain?.value === 1) return
  mesh.layers.enable(CUT_LAYER)
}

// world height into the red channel
const cutMaterial = new ShaderMaterial({
  vertexShader: /* glsl */ `
    varying vec3 vWorld;
    void main() {
      mat4 local = modelMatrix;
      #ifdef USE_INSTANCING
        local = modelMatrix * instanceMatrix;
      #endif
      vec4 world = local * vec4(position, 1.0);
      vWorld = world.xyz;
      gl_Position = projectionMatrix * viewMatrix * world;
    }
  `,
  fragmentShader: /* glsl */ `
    varying vec3 vWorld;
    // only flat-ish faces count, from their shape: the meshes' normals don't always point up
    void main() {
      vec3 n = normalize(cross(dFdx(vWorld), dFdy(vWorld)));
      if (abs(n.y) < 0.3) discard;
      gl_FragColor = vec4(vWorld.y, 1.0, 0.0, 1.0);
    }
  `,
  side: DoubleSide,
})

const CELL = { x: 32, y: 8, z: 32 }
const tintColor = new Color()
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
  private tintOverlay: InstancedMesh[] = []
  private readonly tintMaterials = new Map<number, Material>()
  private readonly lookMaterials = new Map<string, Material>()
  private layer: { meshes: InstancedMesh[]; materials: SceneMaterials; pieces: Map<string, LayerPiece>; dispose: () => void } | null = null
  private selection: number[] = []
  private layerMaterials: SceneMaterials | null = null
  private extent: { minX: number; maxX: number; minZ: number; maxZ: number } | null = null
  private ground: Mesh | null = null
  private environment: string | null = null
  private cutTarget: WebGLRenderTarget | null = null
  private cutTimer = 0
  private previewCall = 0
  private lookMeshes: InstancedMesh[] = []
  private lookHidden: { mesh: InstancedMesh; n: number; matrix: Matrix4 }[] = []
  private readonly upward = new Map<BufferGeometry, BufferGeometry | null>()
  // the preview's version of a piece, for the properties picture
  private looks = new Map<number, PieceParts>()
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

  // terrain is cut where a ground block sits below it (the game reshapes it around the block): the blocks' lowest flat
  // faces (the road, not an arch over it) seen from straight below; in the background after loading and each preview
  private scheduleCut() {
    clearTimeout(this.cutTimer)
    this.cutTimer = window.setTimeout(() => this.updateCut(), 50)
  }

  private updateCut() {
    if (!this.extent || !this.materials) return
    const { minX, maxX, minZ, maxZ } = this.extent
    const [sizeX, sizeZ] = [maxX - minX, maxZ - minZ]
    // 0.5 m per texel: red = road height, green = covered (1); sampled smooth, the cover's 0.5 line traces the block's outline
    const [width, height] = [Math.min(4096, Math.ceil(sizeX * 2)), Math.min(4096, Math.ceil(sizeZ * 2))]
    const type = this.renderer.extensions.has('OES_texture_float_linear') ? FloatType : HalfFloatType
    if (!this.cutTarget) this.cutTarget = new WebGLRenderTarget(width, height, { type, format: RGFormat, minFilter: LinearFilter, magFilter: LinearFilter })
    else this.cutTarget.setSize(width, height)
    const camera = new OrthographicCamera(-sizeX / 2, sizeX / 2, sizeZ / 2, -sizeZ / 2, 1, 20000)
    camera.position.set(minX + sizeX / 2, -10000, minZ + sizeZ / 2)
    camera.up.set(0, 0, 1)
    camera.lookAt(minX + sizeX / 2, 0, minZ + sizeZ / 2)
    camera.layers.set(CUT_LAYER)
    const [background, fog, override] = [this.scene.background, this.scene.fog, this.scene.overrideMaterial]
    const clear = this.renderer.getClearColor(new Color())
    const alpha = this.renderer.getClearAlpha()
    this.scene.background = null
    this.scene.fog = null
    this.scene.overrideMaterial = cutMaterial
    this.renderer.setRenderTarget(this.cutTarget)
    this.renderer.setClearColor(new Color(0, 0, 0), 1)
    this.renderer.clear()
    this.renderer.render(this.scene, camera)
    this.renderer.setRenderTarget(null)
    this.renderer.setClearColor(clear, alpha)
    ;[this.scene.background, this.scene.fog, this.scene.overrideMaterial] = [background, fog, override]
    const box = new Vector4(minX, minZ, sizeX, sizeZ)
    this.materials.setCut(this.cutTarget.texture, box)
    this.layerMaterials?.setCut(this.cutTarget.texture, box)
    this.invalidate()
  }

  // the conversion preview in one step: the converted pieces (a layer of their own, built and textured first),
  // the originals they replace hidden, the rest repainted or coloured
  async showPreview(draw: PreviewDraw | null) {
    const call = ++this.previewCall
    const layer = draw?.changed ? await this.buildLayer(draw.changed) : null
    if (call !== this.previewCall) {
      layer?.dispose()
      return
    }
    this.layer?.dispose()
    this.layer = layer
    for (const mesh of layer?.meshes ?? []) this.scene.add(mesh)
    this.setLook(draw?.looks ?? [])
    this.setTints(draw?.tints ?? [])
    this.layerMaterials = layer?.materials ?? null
    this.addGround(this.environment, layer?.covered ?? [])
    this.setSelection(this.selection)
    this.scheduleCut()
  }

  private async buildLayer({ manifest, geometry: buffer }: { manifest: SceneManifest; geometry: ArrayBuffer }) {
    const materials = new SceneMaterials(manifest.materials, this.renderer, this.invalidate, manifest.environment, this.materials)
    const geometries: BufferGeometry[] = []
    const meshes: InstancedMesh[] = []
    const pieces = manifest.meshes.map((mesh) =>
      mesh.pieces.map((piece) => {
        const geometry = this.pieceGeometry(buffer, piece.offset, piece.vertices, piece.indices, piece.colors)
        geometries.push(geometry)
        return { geometry, material: materials.materials[piece.material] as Material }
      }),
    )
    const byMesh = new Map<string, typeof manifest.instances>()
    const byOrigin = new Map<string, LayerPiece>()
    // ground blocks the conversion added (fillEmptyWithSnow): the base ground goes there
    const covered = new Set<string>()
    for (const instance of manifest.instances) {
      const origin = originOf(instance.info, 'originBlock', 'originItem')
      if (!origin && instance.info.kind.includes('block') && /\bground\b/.test(instance.info.tags ?? '')) {
        const cells = MapScene.cellsOf(pieces[instance.mesh].map((p) => p.geometry), new Matrix4().fromArray(instance.m))
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
          mesh.setColorAt(n, tintColor.setRGB((typeof instance.info.color === 'number' ? instance.info.color : 0) / 8, 0, 0))
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
          this.scene.remove(mesh)
          mesh.dispose()
        }
        for (const geometry of geometries) geometry.dispose()
        materials.dispose()
        this.invalidate()
      },
    }
  }

  materialNames(index: number): (string | null)[] {
    return this.placed[index]?.materials.map((material) => this.materials?.nameOf(material) ?? null) ?? []
  }

  // a preview material, made once per name
  // a removed piece: a faint white ghost of itself
  readonly ghostMaterial: Material = ghost()

  lookMaterial(source: SceneMaterial): Material | null {
    if (!this.materials) return null
    let material = this.lookMaterials.get(source.name)
    if (!material) this.lookMaterials.set(source.name, (material = this.materials.extraMaterial(source)))
    return material
  }

  // the conversion preview drawn in its materials: whole pieces swapped (the native one hidden), or ice laid on their upward faces
  private setLook(changes: LookChange[]) {
    for (const { mesh, n, matrix } of this.lookHidden) {
      mesh.setMatrixAt(n, matrix)
      mesh.instanceMatrix.needsUpdate = true
    }
    this.lookHidden = []
    for (const mesh of this.lookMeshes) {
      this.scene.remove(mesh)
      mesh.dispose()
    }
    this.lookMeshes = []
    this.looks = new Map()

    const groups = new Map<string, { placed: Placed[]; change: LookChange }>()
    for (const change of changes) {
      const placed = this.placed[change.index]
      if (!placed) continue
      if (!change.topsOnly)
        for (const { mesh, n } of placed.slots) {
          this.lookHidden.push({ mesh, n, matrix: placed.matrix })
          mesh.setMatrixAt(n, hidden)
          mesh.instanceMatrix.needsUpdate = true
        }
      // drawn by the changed layer: partsOf takes it from there
      if (change.topsOnly || change.materials.some((m) => m !== null)) this.looks.set(change.index, this.lookOf(placed, change))
      const key = `${placed.meshKey}|${change.topsOnly}|${change.materials.map((m) => m?.uuid ?? '-').join(',')}`
      const group = groups.get(key)
      if (group) group.placed.push(placed)
      else groups.set(key, { placed: [placed], change })
    }
    for (const { placed, change } of groups.values()) {
      placed[0].geometries.forEach((geometry, i) => {
        const swapped = change.materials[i]
        const material = swapped === undefined && !change.topsOnly ? placed[0].materials[i] : swapped
        if (!material) return
        const shape = change.topsOnly ? this.upwardFaces(geometry) : geometry
        if (!shape) return
        const mesh = new InstancedMesh(shape, material, placed.length)
        placed.forEach((p, n) => {
          mesh.setMatrixAt(n, p.matrix)
          mesh.setColorAt(n, tintColor.setRGB(p.tint / 8, 0, 0))
        })
        mesh.matrixAutoUpdate = false
        if (placed[0].ownsGround && placed[0].category !== 'terrain') markCutter(mesh)
        this.scene.add(mesh)
        this.lookMeshes.push(mesh)
      })
    }
    this.invalidate()
  }

  private lookOf(placed: Placed, change: LookChange): PieceParts {
    const parts: PieceParts = change.topsOnly
      ? { geometries: [...placed.geometries], materials: [...placed.materials] }
      : { geometries: [], materials: [] }
    placed.geometries.forEach((geometry, i) => {
      const swapped = change.materials[i]
      const material = swapped === undefined && !change.topsOnly ? placed.materials[i] : swapped
      const shape = material && (change.topsOnly ? this.upwardFaces(geometry) : geometry)
      if (!material || !shape) return
      parts.geometries.push(shape)
      parts.materials.push(material)
    })
    return parts
  }

  // the faces pointing up (local), lifted 2 cm so they sit on the native surface
  private upwardFaces(geometry: BufferGeometry): BufferGeometry | null {
    if (this.upward.has(geometry)) return this.upward.get(geometry)!
    const position = geometry.getAttribute('position')
    const index = geometry.getIndex()
    const count = index ? index.count : position.count
    const at = (i: number) => (index ? index.getX(i) : i)
    const [a, b, c, ab, ac] = [new Vector3(), new Vector3(), new Vector3(), new Vector3(), new Vector3()]
    const keep: number[] = []
    for (let i = 0; i + 2 < count; i += 3) {
      a.fromBufferAttribute(position, at(i))
      b.fromBufferAttribute(position, at(i + 1))
      c.fromBufferAttribute(position, at(i + 2))
      const normal = ab.subVectors(b, a).cross(ac.subVectors(c, a))
      // the converter's top minimum (ItemMesh.TopMinY)
      if (normal.lengthSq() > 0 && normal.normalize().y > 0.02) keep.push(at(i), at(i + 1), at(i + 2))
    }
    let shape: BufferGeometry | null = null
    if (keep.length) {
      shape = geometry.clone()
      shape.setIndex(keep)
      shape.translate(0, 0.02, 0)
      this.geometries.push(shape)
    }
    this.upward.set(geometry, shape)
    return shape
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
    return this.looks.get(index) ?? placed
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

  // the cut pass at a point: [height, cover] (diagnosis)
  cutAt(x: number, z: number): number[] | null {
    if (!this.cutTarget || !this.extent) return null
    const { minX, minZ, maxX, maxZ } = this.extent
    const ix = Math.floor(((x - minX) / (maxX - minX)) * this.cutTarget.width)
    const iy = Math.floor(((z - minZ) / (maxZ - minZ)) * this.cutTarget.height)
    const out: number[] = []
    for (const [dx, dy] of [[0, 0], [-2, 0], [2, 0], [0, -2], [0, 2]]) {
      const pixel = this.cutTarget.texture.type === FloatType ? new Float32Array(4) : new Uint16Array(4)
      this.renderer.readRenderTargetPixels(this.cutTarget, ix + dx, iy + dy, 1, 1, pixel)
      out.push(Math.round(Number(pixel[0]) * 100) / 100, Number(pixel[1]))
    }
    return out
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
    this.previewCall++
    clearTimeout(this.cutTimer)
    this.cutTarget?.dispose()
    this.layer?.dispose()
    for (const geometry of this.geometries) geometry.dispose()
    for (const material of this.ownMaterials) material.dispose()
    this.hoverMaterial.dispose()
    this.selectMaterial.dispose()
    for (const material of this.tintMaterials.values()) material.dispose()
    this.renderer.dispose()
    this.renderer.forceContextLoss()
  }

  private place(meshKey: string, geometries: BufferGeometry[], materials: Material[], m: number[], info: InstanceInfo) {
    // M is row-major with world = local * M, which is three's column-major layout
    // our custom blocks carry no ground of their own, except on hills and cliffs (BlockNames.OnTerrainShape)
    const custom = info.kind === 'custom block'
    const keepsTerrain = custom && /On[A-Z][a-z]+(Cliff|Hill)/.test(String(info.sourceBlock ?? ''))
    const ownsGround = info.kind.includes('block') && (!custom || keepsTerrain) && /\b(ground|terrain)\b/.test(info.tags ?? '')
    const tint = typeof info.color === 'number' ? info.color : 0
    const stacked = info.stacked === true
    const category = categoryOf(info)
    const cuts = info.kind.includes('block') && /\bground\b/.test(info.tags ?? '') && !/\bterrain\b/.test(info.tags ?? '')
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
          mesh.setColorAt(n, tintColor.setRGB(placed.tint / 8, 0, 0))
          placed.slots.push({ mesh, n })
        })
        mesh.matrixAutoUpdate = false
        if (group[0].cuts) markCutter(mesh)
        if (group[0].stacked) drawOver(mesh)
        this.scene.add(mesh)
        const list = this.categoryMeshes.get(category)
        if (list) list.push(mesh)
        else this.categoryMeshes.set(category, [mesh])
      })
    }
  }

  // the cells a piece covers, a hair inside so it doesn't claim the cells it only touches
  private static cellsOf(geometries: BufferGeometry[], matrix: Matrix4) {
    const box = new Box3()
    const part = new Box3()
    for (const geometry of geometries) {
      if (!geometry.boundingBox) geometry.computeBoundingBox()
      box.union(part.copy(geometry.boundingBox!).applyMatrix4(matrix))
    }
    if (box.isEmpty()) return null
    const cell = CELL.x
    return {
      x0: Math.floor((box.min.x + 0.5) / cell), x1: Math.floor((box.max.x - 0.5) / cell),
      z0: Math.floor((box.min.z + 0.5) / cell), z1: Math.floor((box.max.z - 0.5) / cell),
    }
  }

  // cells without a ground or terrain block show the environment's base ground; covered: cells the preview fills
  private addGround(environment: string | null, covered: Iterable<string> = []) {
    this.environment = environment
    if (this.ground) {
      this.scene.remove(this.ground)
      this.ground.geometry.dispose()
      this.ground = null
    }
    const cell = CELL.x
    const owned = new Set<string>(covered)
    let [minX, maxX, minZ, maxZ] = [0, 48, 0, 48]
    // the cut pass covers what the base ground covers
    for (const placed of this.placed) {
      const cells = MapScene.cellsOf(placed.geometries, placed.matrix)
      if (!cells) continue
      const { x0, x1, z0, z1 } = cells
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
    this.extent = { minX: minX * cell, maxX: maxX * cell, minZ: minZ * cell, maxZ: maxZ * cell }
    if (positions.length === 0) return
    const geometry = this.own(new BufferGeometry())
    geometry.setAttribute('position', new BufferAttribute(new Float32Array(positions), 3))
    geometry.setAttribute('normal', new BufferAttribute(new Float32Array(positions.length).map((_, i) => (i % 3 === 1 ? 1 : 0)), 3))
    geometry.setAttribute('uv', new BufferAttribute(new Float32Array((positions.length / 3) * 2), 2))
    this.ground = new Mesh(geometry, this.materials!.ground())
    this.scene.add(this.ground)
    this.invalidate()
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

  // the preview's converted piece that replaces this one, while the preview shows it
  private converted(placed: Placed): LayerPiece | null {
    return (placed.origin && this.layer?.pieces.get(placed.origin)) || null
  }

  // in the preview, converted pieces are outlined as converted
  private replaceOverlay(old: InstancedMesh[], indices: number[], material: Material): InstancedMesh[] {
    for (const mesh of old) {
      this.scene.remove(mesh)
      mesh.dispose()
    }
    const groups = new Map<string, { geometries: BufferGeometry[]; matrix: Matrix4 }[]>()
    for (const index of indices) {
      const placed = this.placed[index]
      if (!placed) continue
      const shown = this.converted(placed) ?? { key: placed.meshKey, geometries: placed.geometries, matrix: placed.matrix }
      const group = groups.get(shown.key)
      if (group) group.push(shown)
      else groups.set(shown.key, [shown])
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
  }

  private readonly onBlur = () => {
    this.keys.clear()
    this.controls.enabled = true
    if (this.painting) this.events.stroke('end')
    this.painting = null
  }
}

// instance colours carry the block colour index, not a colour: left out, or the ghost turns black
function ghost() {
  const material = new MeshBasicMaterial({ color: 0xffffff, opacity: 0.18, transparent: true, depthWrite: false, fog: false })
  material.onBeforeCompile = (shader) => {
    shader.fragmentShader = shader.fragmentShader.replace('#include <color_fragment>', '')
  }
  return material
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
