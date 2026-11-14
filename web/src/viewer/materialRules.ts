import type { SceneMaterial, SceneTexture } from '../api'

// what a material looks like from its name and shader, before and without its textures
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

export const fallbackColor = (name: string): [number, number, number] => {
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

export const terrainOf = (environment: string | null): Terrain => terrains[environment ?? 'Stadium2020'] ?? terrains.Stadium2020

export const layerImage = (environment: string | null, layer: string) => `${environment}/Media/Texture/Image/${layer}_D.dds`

// measured: vista zone tops sit 2 m above the base cell (01 RedIsland, 02 GreenCoast); Stadium's ground is at 8 m
export const groundHeight = (environment: string | null): number => (environment ?? 'Stadium2020').startsWith('Stadium') ? 8 : 2

// a default zone of water (BlueBay's Sea): its surface as measured on 09's beaches
export const isWaterZone = (zone: string | null) => /Sea|Water|Lake/.test(zone ?? '')
export const waterHeight = 7

export const isTerrainShader = (name: string) => name.includes('_Ids')

export type Mapping = 'uv' | 'top' | 'side'

// projected shaders (PyPxz): unmarked textures go on top, then on the slopes; decals keep their own UVs
const mappingOf = (name: string, shader: string, taken: Partial<Record<Mapping, unknown>>): Mapping => {
  if (name.includes('Py')) return 'top'
  if (name.includes('Pxz')) return 'side'
  if (!shader.includes('PyPxz') || shader.includes('Decal')) return 'uv'
  return taken.top ? 'side' : 'top'
}

export const diffuseMaps = (material: SceneMaterial): Partial<Record<Mapping, SceneTexture>> => {
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
export const isLedPanel = (material: SceneMaterial) => material.textures.some((t) => t.role === 'D' && t.name.startsWith('LightCells'))

export const isMaskedDecal = (material: SceneMaterial) => {
  const decal = `${material.name} ${material.shader ?? ''}`.toLowerCase().includes('decal')
  return decal && material.textures.some((texture) => texture.role === 'M')
}
