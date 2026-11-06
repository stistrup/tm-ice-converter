import {
  Color,
  CompressedTexture,
  DataTexture,
  DoubleSide,
  RepeatWrapping,
  ShaderMaterial,
  SRGBColorSpace,
  NoColorSpace,
  AdditiveBlending,
  Texture,
  LinearMipmapLinearFilter,
  LinearFilter,
  Vector4,
  type CompressedPixelFormat,
  type Material,
  type WebGLRenderer,
} from 'three'
import { DDSLoader } from 'three/addons/loaders/DDSLoader.js'
import { textureUrl, type SceneMaterial, type SceneTexture } from '../api'

const vertexShader = /* glsl */ `
  uniform float flipV;
  attribute vec4 cv;
  varying vec4 vCv;
  varying vec3 vWorld;
  varying vec3 vNormal;
  varying vec2 vUv;
  varying float vTint;

  void main() {
    mat4 world = modelMatrix;
    #ifdef USE_INSTANCING_COLOR
      vTint = instanceColor.r * 8.0;
    #else
      vTint = 0.0;
    #endif
    #ifdef USE_INSTANCING
      world = modelMatrix * instanceMatrix;
    #endif
    vec4 position4 = world * vec4(position, 1.0);
    vWorld = position4.xyz;
    vNormal = mat3(world) * normal;
    vUv = flipV > 0.5 ? vec2(uv.x, 1.0 - uv.y) : uv;
    vCv = cv;
    gl_Position = projectionMatrix * viewMatrix * position4;
  }
`

const fragmentShader = /* glsl */ `
  uniform sampler2D mapUv;
  uniform sampler2D mapTop;
  uniform sampler2D mapSide;
  uniform vec3 hasMap;
  uniform vec3 baseColor;
  uniform sampler2D mapHue;
  uniform float hasHue;
  uniform sampler2D mapIllum;
  uniform float hasIllum;
  uniform float additive;
  uniform float solidGlow;
  uniform vec3 tints[5];
  uniform vec3 sideColor;
  uniform float terrain;
  uniform float cutout;
  uniform float decal;
  uniform float ice;
  uniform float opacity;
  uniform float water;
  uniform float greyGreen;
  // the ground blocks' road surface (MapScene's cut pass): terrain above it inside their footprint is cut away
  uniform sampler2D cutMap;
  uniform vec4 cutBox;
  uniform float hasCut;

  varying vec3 vWorld;
  varying vec3 vNormal;
  varying vec2 vUv;
  varying float vTint;
  varying vec4 vCv;

  const float TILE = 1.0 / 32.0;

  void main() {
    vec3 n = dot(vNormal, vNormal) > 0.0001
      ? normalize(vNormal)
      : normalize(cross(dFdx(vWorld), dFdy(vWorld)));
    if (!gl_FrontFacing) n = -n;
    // only the surface: the volume's sides would show through
    if (water > 0.5 && abs(n.y) < 0.7) discard;
    if (terrain > 0.5 && hasCut > 0.5) {
      vec2 cuv = (vWorld.xz - cutBox.xy) / cutBox.zw;
      if (cuv.x >= 0.0 && cuv.x <= 1.0 && cuv.y >= 0.0 && cuv.y <= 1.0) {
        // blended between texels: height over cover is the covered texels' own height
        vec2 cut = texture2D(cutMap, cuv).rg;
        // level with the road counts too: inside a block the road is what shows
        if (cut.g > 0.5 && vWorld.y > cut.r / cut.g - 0.3) discard;
      }
    }

    vec4 albedo = vec4(baseColor, 1.0);
    if (terrain > 0.5) albedo.rgb = mix(sideColor, baseColor, smoothstep(0.55, 0.8, n.y));
    if (hasMap.x > 0.5) albedo = texture2D(mapUv, vUv);
    if (hasMap.z > 0.5) {
      vec2 sideUv = abs(n.x) > abs(n.z) ? vWorld.zy : vWorld.xy;
      albedo = texture2D(mapSide, sideUv * TILE);
    }
    if (hasMap.y > 0.5) {
      float up = smoothstep(0.55, 0.8, n.y);
      vec4 top = texture2D(mapTop, vec2(vWorld.x, -vWorld.z) * TILE);
      albedo = hasMap.x + hasMap.z > 0.5 ? mix(albedo, top, up) : top;
    }

    // _Hue shaders (Custom* library materials): the texture is brightness in its green channel, the hue (white here) colours it
    if (greyGreen > 0.5) albedo.rgb = vec3(albedo.g);

    // the block's colour repaints the hue mask's area: alpha = how much, green = brightness; Default keeps the painted green
    int tint = int(vTint + 0.5);
    if (hasHue > 0.5 && tint >= 1 && tint <= 5) {
      vec4 hue = texture2D(mapHue, vUv);
      albedo.rgb = mix(albedo.rgb, tints[tint - 1] * hue.g, hue.a);
    }

    if (ice > 0.5) {
      // rink markings are faint: boost them
      // the numbers are for raw texture values
      vec3 raw = pow(albedo.rgb, vec3(1.0 / 2.2));
      vec3 boosted = clamp(raw * 2.0 + vec3(0.42, 0.62, 0.88) - vec3(0.70, 0.74, 0.78) * 2.0, 0.0, 1.0);
      albedo = vec4(hasMap.x > 0.5 ? boosted : vec3(0.42, 0.62, 0.88), 1.0);
    }

    float alpha = decal > 0.5 ? albedo.a : 1.0;
    if (cutout > 0.5 && albedo.a < 0.5) discard;
    if (alpha < 0.01) discard;

    vec3 sun = normalize(vec3(0.45, 0.8, 0.35));
    vec3 light = mix(vec3(0.33, 0.33, 0.36), vec3(0.72, 0.8, 0.9), n.y * 0.5 + 0.5)
      + vec3(0.62, 0.58, 0.5) * max(dot(n, sun), 0.0);
    // self-lit parts (sign arrows, timers, screens) glow on top; additive FX (waypoint triggers) are only that glow
    vec3 illum = hasIllum < 0.5 ? vec3(0.0)
      // the smallest mip = the texture's average: its background colour without the detail
      : solidGlow > 0.5 ? textureLod(mapIllum, vUv, 16.0).rgb
      : texture2D(mapIllum, vUv).rgb;
    // additive FX (TAddModCV, waypoint triggers): glow x vertex colour; the colours are 8-bit sRGB like textures
    vec3 lit = additive > 0.5 ? illum * pow(vCv.rgb, vec3(2.2)) : albedo.rgb * light + illum;
    gl_FragColor = vec4(lit, alpha * opacity);
    #include <colorspace_fragment>
  }
`

const fallbackColors: [string, [number, number, number]][] = [
  ['ice', [0.62, 0.78, 0.9]],
  ['snow', [0.9, 0.92, 0.95]],
  ['decohill', [0.88, 0.9, 0.93]],
  ['grass', [0.25, 0.45, 0.15]],
  ['dirt', [0.45, 0.3, 0.18]],
  ['water', [0.1, 0.3, 0.4]],
  ['border', [0.85, 0.85, 0.85]],
  ['tech', [0.35, 0.37, 0.4]],
  ['wall', [0.5, 0.5, 0.52]],
  ['deco', [0.55, 0.55, 0.55]],
]

function fallbackColor(name: string): [number, number, number] {
  const lower = name.toLowerCase()
  return fallbackColors.find(([key]) => lower.includes(key))?.[1] ?? [0.6, 0.6, 0.6]
}

// terrain shaders pick layers by per-vertex ids, no texture of their own: ground layer on top, cliff on the sides.
// Colours: linear averages of those layers, shown until the textures load; Stadium has none, guessed
interface Terrain {
  top: [number, number, number]
  side: [number, number, number]
  layers?: { top: string; side: string }
}

const terrains: Record<string, Terrain> = {
  RedIsland: { top: [0.351, 0.222, 0.16], side: [0.333, 0.141, 0.082], layers: { top: 'Dirt', side: 'CliffPxz' } },
  GreenCoast: { top: [0.14, 0.162, 0.089], side: [0.353, 0.23, 0.182], layers: { top: 'Grass', side: 'Cliff' } },
  BlueBay: { top: [0.131, 0.185, 0.053], side: [0.255, 0.232, 0.174], layers: { top: 'Land', side: 'CliffPxz' } },
  WhiteShore: { top: [0.563, 0.644, 0.731], side: [0.476, 0.504, 0.545], layers: { top: 'Land', side: 'LandRocky' } },
  Stadium2020: { top: [0.16, 0.3, 0.09], side: [0.5, 0.46, 0.4] },
}

function terrainOf(environment: string | null): Terrain {
  return terrains[environment ?? 'Stadium2020'] ?? terrains.Stadium2020
}

function layerImage(environment: string | null, layer: string) {
  return `${environment}/Media/Texture/Image/${layer}_D.dds`
}

// measured: vista zone tops sit 2 m above the base cell (01 RedIsland, 02 GreenCoast); Stadium's ground is at 8 m
export function groundHeight(environment: string | null): number {
  return (environment ?? 'Stadium2020').startsWith('Stadium') ? 8 : 2
}

function isTerrainShader(name: string) {
  return name.includes('_Ids')
}

type Mapping = 'uv' | 'top' | 'side'

// projected shaders (PyPxz): unmarked textures go on top, then on the slopes; decals keep their own UVs
function mappingOf(name: string, shader: string, taken: Partial<Record<Mapping, unknown>>): Mapping {
  if (name.includes('Py')) return 'top'
  if (name.includes('Pxz')) return 'side'
  if (!shader.includes('PyPxz') || shader.includes('Decal')) return 'uv'
  return taken.top ? 'side' : 'top'
}

function diffuseMaps(material: SceneMaterial): Partial<Record<Mapping, SceneTexture>> {
  const found: Partial<Record<Mapping, SceneTexture>> = {}
  for (const texture of material.textures) {
    // only images the loader reads: decal sets and the like would hide a usable one listed later
    if (texture.role !== 'D' || !texture.image?.toLowerCase().endsWith('.dds')) continue
    const mapping = mappingOf(texture.name, material.shader ?? '', found)
    if (!found[mapping] || (mapping === 'uv' && texture.name.startsWith(material.name))) found[mapping] = texture
  }
  return found
}

// LED screens: LightCells* is one tile of dots the game repeats densely across the panel; here a dark panel under its glow
function isLedPanel(material: SceneMaterial) {
  return material.textures.some((t) => t.role === 'D' && t.name.startsWith('LightCells'))
}

function isMaskedDecal(material: SceneMaterial) {
  const decal = `${material.name} ${material.shader ?? ''}`.toLowerCase().includes('decal')
  return decal && material.textures.some((texture) => texture.role === 'M')
}

function blankTexture(): Texture {
  const texture = new DataTexture(new Uint8Array([255, 255, 255, 255]), 1, 1)
  texture.needsUpdate = true
  return texture
}

const blank = blankTexture()

export function flatMaterial(color: [number, number, number]): ShaderMaterial {
  return new ShaderMaterial({
    vertexShader,
    fragmentShader,
    side: DoubleSide,
    uniforms: {
      mapUv: { value: blank },
      mapTop: { value: blank },
      mapSide: { value: blank },
      hasMap: { value: new Color(0, 0, 0) },
      baseColor: { value: new Color(...color) },
      sideColor: { value: new Color(...color) },
      terrain: { value: 0 },
      cutout: { value: 0 },
      decal: { value: 0 },
      ice: { value: 0 },
      opacity: { value: 1 },
      flipV: { value: 0 },
      mapHue: { value: blank },
      hasHue: { value: 0 },
      mapIllum: { value: blank },
      hasIllum: { value: 0 },
      additive: { value: 0 },
      solidGlow: { value: 0 },
      tints: { value: Array.from({ length: 5 }, () => new Color(1, 1, 1)) },
      water: { value: 0 },
      greyGreen: { value: 0 },
      cutMap: { value: blank },
      cutBox: { value: new Vector4(0, 0, 1, 1) },
      hasCut: { value: 0 },
    },
  })
}

interface TextureJob {
  material: ShaderMaterial
  mapping: Mapping | 'hue' | 'illum'
  path: string
}

// fails quietly: a material keeps its plain colour
export class SceneMaterials {
  readonly materials: ShaderMaterial[]
  private readonly loader = new DDSLoader()
  private readonly cache = new Map<string, Promise<CompressedTexture | null>>()
  private readonly extraJobs: TextureJob[] = []
  private readonly extra: ShaderMaterial[] = []
  private cancelled = false

  constructor(
    private readonly source: SceneMaterial[],
    private readonly renderer: WebGLRenderer,
    private readonly onTexture: () => void,
    private readonly environment: string | null,
    // textures come from (and stay with) this one: a preview layer reuses the scene's
    private readonly shared: SceneMaterials | null = null,
  ) {
    this.materials = source.map((material) => this.create(material))
  }

  private create(source: SceneMaterial): ShaderMaterial {
    const lowerName = source.name.toLowerCase()
    const shader = source.shader ?? ''
    const isDecal = lowerName.includes('decal') || shader.toLowerCase().includes('decal')
    const isCutout = source.name.includes('Fence')
    const material = flatMaterial(fallbackColor(source.name))
    material.uniforms.cutout.value = isCutout ? 1 : 0
    material.uniforms.decal.value = isDecal ? 1 : 0
    material.uniforms.ice.value = shader.includes('Layered') ? 1 : 0
    material.uniforms.greyGreen.value = shader.endsWith('_Hue') ? 1 : 0
    // timer digit slots (ChronoFinish-00-01-00…) show one digit of an atlas the game shifts at runtime:
    // not faked, they glow in the atlas's background colour
    const digitSlot = /^Chrono\w*-\d+-\d+-\d+$/.test(source.name)
    material.uniforms.solidGlow.value = digitSlot ? 1 : 0
    const illum = source.textures.find((t) => t.role === 'I' && t.image?.toLowerCase().endsWith('.dds'))
    if (illum) this.extraJobs.push({ material, mapping: 'illum', path: illum.image! })
    // additive FX shaders (TAdd…): glow only, seen through; hidden until their glow loads
    if (shader.includes('TAdd')) {
      material.uniforms.additive.value = 1
      material.transparent = true
      material.depthWrite = false
      material.blending = AdditiveBlending
      material.visible = false
    }
    if (source.tints && source.tints.length >= 5) {
      const tints = material.uniforms.tints.value as Color[]
      source.tints.slice(0, 5).forEach((hex, i) => tints[i].setStyle(hex))
      const hue = source.textures.find((t) => t.role === 'HueMask' && t.image?.toLowerCase().endsWith('.dds'))
      if (hue) this.extraJobs.push({ material, mapping: 'hue', path: hue.image! })
    }
    // some shader families read V the other way, each checked on 01 against the game: DispIn (road walls), decals (curb atlas rows);
    // plain CubeOut not (flagpoles)
    material.uniforms.flipV.value = shader.includes('DispIn') || shader.includes('Decal') ? 1 : 0
    if (isTerrainShader(source.name)) this.makeTerrain(material, true)
    if (isLedPanel(source)) (material.uniforms.baseColor.value as Color).setRGB(0.02, 0.02, 0.02)
    // the game's water reflects and fades with depth; here one even, opaque colour
    if (shader.includes('Water')) {
      ;(material.uniforms.baseColor.value as Color).setRGB(0.05, 0.16, 0.24)
      material.uniforms.water.value = 1
    }
    if (isDecal) {
      material.transparent = true
      material.depthWrite = false
      material.polygonOffset = true
      material.polygonOffsetFactor = -1
      material.polygonOffsetUnits = -1
      // nothing to draw without its texture
      material.visible = false
    }
    return material
  }

  // a material outside the scene's list (the preview's ice looks), textures loaded right away
  extraMaterial(source: SceneMaterial): ShaderMaterial {
    const material = this.create(source)
    this.extra.push(material)
    const jobs: TextureJob[] = Object.entries(diffuseMaps(source)).map(([mapping, texture]) => ({
      material,
      mapping: mapping as Mapping,
      path: texture.image!,
    }))
    jobs.push(...this.extraJobs.filter((job) => job.material === material))
    for (const job of jobs) void this.apply(job)
    return material
  }

  // the cut pass's result on every terrain material
  setCut(map: Texture | null, box: Vector4) {
    for (const material of [...this.materials, ...this.extra]) {
      if (material.uniforms.terrain.value !== 1) continue
      material.uniforms.cutMap.value = map ?? blank
      ;(material.uniforms.cutBox.value as Vector4).copy(box)
      material.uniforms.hasCut.value = map ? 1 : 0
    }
  }

  nameOf(material: Material): string | null {
    const index = this.materials.indexOf(material as ShaderMaterial)
    return index < 0 ? null : this.source[index].name
  }

  // flat base ground for cells without a block
  ground(): ShaderMaterial {
    const material = flatMaterial(terrainOf(this.environment).top)
    this.extra.push(material)
    this.makeTerrain(material, false)
    return material
  }

  private makeTerrain(material: ShaderMaterial, withSides: boolean) {
    const terrain = terrainOf(this.environment)
    ;(material.uniforms.baseColor.value as Color).setRGB(...terrain.top)
    ;(material.uniforms.sideColor.value as Color).setRGB(...(withSides ? terrain.side : terrain.top))
    material.uniforms.terrain.value = 1
    if (!terrain.layers) return
    this.extraJobs.push({ material, mapping: 'top', path: layerImage(this.environment, terrain.layers.top) })
    if (withSides) this.extraJobs.push({ material, mapping: 'side', path: layerImage(this.environment, terrain.layers.side) })
  }

  // all of them, a few at a time
  async loadTextures() {
    const jobs = [
      // a paint decal takes its shape from a mask (M) the viewer doesn't read: drawn without it, it's a plain rectangle
      ...this.source.flatMap((material, index) =>
        (isMaskedDecal(material) || isLedPanel(material) ? [] : Object.entries(diffuseMaps(material))).map(([mapping, texture]) => ({
          material: this.materials[index],
          mapping: mapping as Mapping,
          path: texture.image!,
        })),
      ),
      ...this.extraJobs,
    ]
    const worker = async () => {
      for (let job = jobs.shift(); job && !this.cancelled; job = jobs.shift()) await this.apply(job)
    }
    await Promise.all(Array.from({ length: 6 }, worker))
  }

  private async apply({ material, mapping, path }: TextureJob) {
    const texture = await this.texture(path)
    if (!texture || this.cancelled) return
    const hasMap = material.uniforms.hasMap.value as Color
    if (mapping === 'illum') {
      material.uniforms.mapIllum.value = texture
      material.uniforms.hasIllum.value = 1
      material.visible = true
      this.onTexture()
      return
    }
    if (mapping === 'hue') {
      texture.colorSpace = NoColorSpace
      material.uniforms.mapHue.value = texture
      material.uniforms.hasHue.value = 1
      this.onTexture()
      return
    }
    if (mapping === 'uv') {
      material.uniforms.mapUv.value = texture
      hasMap.r = 1
    } else if (mapping === 'top') {
      material.uniforms.mapTop.value = texture
      hasMap.g = 1
    } else {
      material.uniforms.mapSide.value = texture
      hasMap.b = 1
    }
    material.visible = true
    this.onTexture()
  }

  private texture(path: string): Promise<CompressedTexture | null> {
    if (this.shared) return this.shared.texture(path)
    let pending = this.cache.get(path)
    if (!pending) {
      pending = this.fetchTexture(path)
      this.cache.set(path, pending)
    }
    return pending
  }

  private async fetchTexture(path: string): Promise<CompressedTexture | null> {
    if (!path.toLowerCase().endsWith('.dds')) return null
    try {
      const response = await fetch(textureUrl(path))
      if (!response.ok) return null
      const dds = this.loader.parse(await response.arrayBuffer(), true)
      if (!dds.format || dds.mipmaps.length === 0) return null
      const texture = new CompressedTexture(dds.mipmaps, dds.width, dds.height, dds.format as CompressedPixelFormat)
      texture.wrapS = texture.wrapT = RepeatWrapping
      texture.colorSpace = SRGBColorSpace
      texture.minFilter = dds.mipmaps.length > 1 ? LinearMipmapLinearFilter : LinearFilter
      texture.magFilter = LinearFilter
      texture.anisotropy = Math.min(8, this.renderer.capabilities.getMaxAnisotropy())
      texture.needsUpdate = true
      return texture
    } catch {
      return null
    }
  }

  dispose() {
    this.cancelled = true
    for (const material of [...this.materials, ...this.extra]) material.dispose()
    for (const pending of this.cache.values()) void pending.then((texture) => texture?.dispose())
  }
}
