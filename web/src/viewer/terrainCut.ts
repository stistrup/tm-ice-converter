import {
  Color,
  DoubleSide,
  FloatType,
  HalfFloatType,
  LinearFilter,
  OrthographicCamera,
  RGFormat,
  ShaderMaterial,
  Vector4,
  WebGLRenderTarget,
  type InstancedMesh,
  type Material,
  type Scene,
  type Texture,
  type WebGLRenderer,
} from 'three'

const CUT_LAYER = 1

export interface Extent {
  minX: number
  maxX: number
  minZ: number
  maxZ: number
}

// polygon offset only while this mesh draws: the material stays shared
export const drawOver = (mesh: InstancedMesh) => {
  mesh.onBeforeRender = (_r, _s, _c, _g, material) => {
    material.polygonOffset = true
    material.polygonOffsetFactor = -1
    material.polygonOffsetUnits = -4
  }
  mesh.onAfterRender = (_r, _s, _c, _g, material) => {
    material.polygonOffset = false
  }
}

// a placed block's own terrain faces (dirt banks) aren't the map's terrain: no cut while this mesh draws, the material stays shared
export const keepUncut = (mesh: InstancedMesh) => {
  const [before, after] = [mesh.onBeforeRender, mesh.onAfterRender]
  let held = 0
  mesh.onBeforeRender = (r, s, c, g, material, group) => {
    before.call(mesh, r, s, c, g, material, group)
    const uniforms = (material as ShaderMaterial).uniforms
    if (uniforms?.terrain?.value !== 1) return
    held = uniforms.hasCut.value
    uniforms.hasCut.value = 0
    ;(material as ShaderMaterial).uniformsNeedUpdate = true
  }
  mesh.onAfterRender = (r, s, c, g, material, group) => {
    after.call(mesh, r, s, c, g, material, group)
    const uniforms = (material as ShaderMaterial).uniforms
    if (uniforms?.terrain?.value !== 1) return
    uniforms.hasCut.value = held
    ;(material as ShaderMaterial).uniformsNeedUpdate = true
  }
}

// a piece with terrain surfaces of its own is a terrain shape (cliff deco), not something placed on terrain: it cuts nothing
export const isTerrainShaped = (materials: Material[]) => materials.some((m) => (m as ShaderMaterial).uniforms?.terrain?.value === 1)

// ground blocks (not terrain) are what the terrain gets cut around
export const markCutter = (mesh: InstancedMesh) => {
  const material = mesh.material as ShaderMaterial
  if (material.uniforms?.terrain?.value === 1 || material.userData?.noCut) return
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

// terrain is cut where a ground block sits below it (the game reshapes it around the block): the blocks' lowest flat
// faces (the road, not an arch over it) seen from straight below; in the background after loading and each preview
export class TerrainCut {
  private target: WebGLRenderTarget | null = null
  private timer = 0
  private extent: Extent | null = null

  constructor(
    private readonly renderer: WebGLRenderer,
    private readonly scene: Scene,
    private readonly done: (map: Texture, box: Vector4) => void,
  ) {}

  schedule(extent: Extent) {
    this.extent = extent
    clearTimeout(this.timer)
    this.timer = window.setTimeout(() => this.update(), 50)
  }

  private update() {
    if (!this.extent) return
    const { minX, maxX, minZ, maxZ } = this.extent
    const [sizeX, sizeZ] = [maxX - minX, maxZ - minZ]
    // 0.5 m per texel: red = road height, green = covered (1); sampled smooth, the cover's 0.5 line traces the block's outline
    const [width, height] = [Math.min(4096, Math.ceil(sizeX * 2)), Math.min(4096, Math.ceil(sizeZ * 2))]
    const type = this.renderer.extensions.has('OES_texture_float_linear') ? FloatType : HalfFloatType
    if (!this.target) this.target = new WebGLRenderTarget(width, height, { type, format: RGFormat, minFilter: LinearFilter, magFilter: LinearFilter })
    else this.target.setSize(width, height)
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
    this.renderer.setRenderTarget(this.target)
    this.renderer.setClearColor(new Color(0, 0, 0), 1)
    this.renderer.clear()
    this.renderer.render(this.scene, camera)
    this.renderer.setRenderTarget(null)
    this.renderer.setClearColor(clear, alpha)
    ;[this.scene.background, this.scene.fog, this.scene.overrideMaterial] = [background, fog, override]
    this.done(this.target.texture, new Vector4(minX, minZ, sizeX, sizeZ))
  }

  // at a point: [height, cover] (diagnosis)
  at(x: number, z: number): number[] | null {
    if (!this.target || !this.extent) return null
    const { minX, minZ, maxX, maxZ } = this.extent
    const ix = Math.floor(((x - minX) / (maxX - minX)) * this.target.width)
    const iy = Math.floor(((z - minZ) / (maxZ - minZ)) * this.target.height)
    const out: number[] = []
    for (const [dx, dy] of [[0, 0], [-2, 0], [2, 0], [0, -2], [0, 2]]) {
      const pixel = this.target.texture.type === FloatType ? new Float32Array(4) : new Uint16Array(4)
      this.renderer.readRenderTargetPixels(this.target, ix + dx, iy + dy, 1, 1, pixel)
      out.push(Math.round(Number(pixel[0]) * 100) / 100, Number(pixel[1]))
    }
    return out
  }

  dispose() {
    clearTimeout(this.timer)
    this.target?.dispose()
  }
}
