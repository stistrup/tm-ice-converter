import {
  Color,
  CompressedTexture,
  NoColorSpace,
  AdditiveBlending,
  RepeatWrapping,
  SRGBColorSpace,
  Texture,
  LinearMipmapLinearFilter,
  LinearFilter,
  Vector4,
  type CompressedPixelFormat,
  type Material,
  type ShaderMaterial,
  type WebGLRenderer,
} from 'three'
import { DDSLoader } from 'three/addons/loaders/DDSLoader.js'
import { textureUrl, type SceneMaterial } from '../api'
import { diffuseMaps, fallbackColor, isLedPanel, isMaskedDecal, isTerrainShader, layerImage, terrainOf, type Mapping } from './materialRules'
import { blank, flatMaterial } from './sceneShader'

export { flatMaterial } from './sceneShader'
export { groundHeight } from './materialRules'

interface TextureJob {
  material: ShaderMaterial
  mapping: Mapping | 'hue' | 'illum'
  path: string
}

// fails quietly: a material keeps its plain colour
const WATER: [number, number, number] = [0.05, 0.16, 0.24]

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
    const isCutout = source.name.includes('Fence') || shader === 'VegetLeaf'
    const material = flatMaterial(fallbackColor(source.name))
    material.uniforms.cutout.value = isCutout ? 1 : 0
    // plants, fences and water aren't ground: the terrain isn't cut under them
    material.userData.noCut = isCutout || shader.startsWith('Veget') || shader.includes('Water')
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
      if (hue?.name.includes('Pxz')) material.uniforms.hueSide.value = 1
    }
    // some shader families read V the other way, each checked on 01 against the game: DispIn (road walls), decals (curb atlas rows);
    // plain CubeOut not (flagpoles)
    material.uniforms.flipV.value = shader.includes('DispIn') || shader.includes('Decal') ? 1 : 0
    if (isTerrainShader(source.name)) this.makeTerrain(material, true)
    if (isLedPanel(source)) (material.uniforms.baseColor.value as Color).setRGB(0.02, 0.02, 0.02)
    // the game's water reflects and fades with depth; here one even, opaque colour
    if (shader.includes('Water')) {
      ;(material.uniforms.baseColor.value as Color).setRGB(...WATER)
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
  // as the game's water materials are drawn here: one even colour
  water(): ShaderMaterial {
    const material = flatMaterial(WATER)
    material.uniforms.water.value = 1
    this.extra.push(material)
    return material
  }

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
