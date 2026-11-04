import {
  Color,
  CompressedTexture,
  DataTexture,
  DoubleSide,
  RepeatWrapping,
  ShaderMaterial,
  SRGBColorSpace,
  Texture,
  LinearMipmapLinearFilter,
  LinearFilter,
  type CompressedPixelFormat,
  type WebGLRenderer,
} from 'three'
import { DDSLoader } from 'three/addons/loaders/DDSLoader.js'
import { textureUrl, type SceneMaterial, type SceneTexture } from '../api'

const vertexShader = /* glsl */ `
  varying vec3 vWorld;
  varying vec3 vNormal;
  varying vec2 vUv;

  void main() {
    mat4 world = modelMatrix;
    #ifdef USE_INSTANCING
      world = modelMatrix * instanceMatrix;
    #endif
    vec4 position4 = world * vec4(position, 1.0);
    vWorld = position4.xyz;
    vNormal = mat3(world) * normal;
    vUv = uv;
    gl_Position = projectionMatrix * viewMatrix * position4;
  }
`

const fragmentShader = /* glsl */ `
  uniform sampler2D mapUv;
  uniform sampler2D mapTop;
  uniform sampler2D mapSide;
  uniform vec3 hasMap;
  uniform vec3 baseColor;
  uniform float cutout;
  uniform float decal;
  uniform float ice;

  varying vec3 vWorld;
  varying vec3 vNormal;
  varying vec2 vUv;

  const float TILE = 1.0 / 32.0;

  void main() {
    vec3 n = dot(vNormal, vNormal) > 0.0001
      ? normalize(vNormal)
      : normalize(cross(dFdx(vWorld), dFdy(vWorld)));
    if (!gl_FrontFacing) n = -n;

    vec4 albedo = vec4(baseColor, 1.0);
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
    gl_FragColor = vec4(albedo.rgb * light, alpha);
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

type Mapping = 'uv' | 'top' | 'side'

function mappingOf(name: string): Mapping {
  return name.includes('Py') ? 'top' : name.includes('Pxz') ? 'side' : 'uv'
}

function diffuseMaps(material: SceneMaterial): Partial<Record<Mapping, SceneTexture>> {
  const found: Partial<Record<Mapping, SceneTexture>> = {}
  for (const texture of material.textures) {
    if (texture.role !== 'D' || !texture.image) continue
    const mapping = mappingOf(texture.name)
    if (!found[mapping] || (mapping === 'uv' && texture.name.startsWith(material.name))) found[mapping] = texture
  }
  return found
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
      cutout: { value: 0 },
      decal: { value: 0 },
      ice: { value: 0 },
    },
  })
}

// fails quietly: a material keeps its plain colour
export class SceneMaterials {
  readonly materials: ShaderMaterial[]
  private readonly loader = new DDSLoader()
  private readonly cache = new Map<string, Promise<CompressedTexture | null>>()
  private cancelled = false

  constructor(
    private readonly source: SceneMaterial[],
    private readonly renderer: WebGLRenderer,
    private readonly onTexture: () => void,
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

  // all of them, a few at a time
  async loadTextures() {
    const jobs = this.source.flatMap((material, index) =>
      Object.entries(diffuseMaps(material)).map(([mapping, texture]) => ({
        index,
        mapping: mapping as Mapping,
        path: texture.image!,
      })),
    )
    const worker = async () => {
      for (let job = jobs.shift(); job && !this.cancelled; job = jobs.shift()) await this.apply(job)
    }
    await Promise.all(Array.from({ length: 6 }, worker))
  }

  private async apply({ index, mapping, path }: { index: number; mapping: Mapping; path: string }) {
    const texture = await this.texture(path)
    if (!texture || this.cancelled) return
    const material = this.materials[index]
    const hasMap = material.uniforms.hasMap.value as Color
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

  private texture(path: string) {
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
    for (const material of this.materials) material.dispose()
    for (const pending of this.cache.values()) void pending.then((texture) => texture?.dispose())
  }
}
