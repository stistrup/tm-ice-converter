export interface MapEntry {
  path: string
  name: string
  folder: string
  size: number
  modified: string
}

export interface SceneTexture {
  role: string
  name: string
  image: string | null
}

export interface SceneMaterial {
  name: string
  shader: string | null
  textures: SceneTexture[]
  // colour table, one per block colour White..Black
  tints: string[] | null
}

export interface ScenePiece {
  material: number
  offset: number
  vertices: number
  indices: number
  // rgba per vertex after the indices
  colors: boolean
}

export interface SceneMesh {
  key: string
  pieces: ScenePiece[]
}

export type InstanceInfo = Record<string, unknown> & { kind: string; name: string; tags?: string }

export interface SceneInstance {
  mesh: number
  m: number[]
  info: InstanceInfo
}

export interface SceneMarker {
  mesh: string
  m: number[]
  info: InstanceInfo
}

export interface SceneManifest {
  map: string
  environment: string | null
  geometryBytes: number
  notExtracted: number
  materials: SceneMaterial[]
  meshes: SceneMesh[]
  instances: SceneInstance[]
  placeholders: SceneMarker[]
  boxes: SceneMarker[]
  skipped: Record<string, number>
}

async function check(response: Response): Promise<Response> {
  if (!response.ok) throw new Error(await response.text())
  return response
}

export async function fetchMaps(): Promise<MapEntry[]> {
  return (await check(await fetch('/api/maps'))).json()
}

export async function fetchScene(map: string, rebuild: boolean): Promise<SceneManifest> {
  const query = new URLSearchParams({ map })
  if (rebuild) query.set('rebuild', '1')
  return (await check(await fetch(`/api/scene?${query}`))).json()
}

export async function fetchGeometry(
  map: string,
  total: number,
  onProgress: (fraction: number) => void,
): Promise<ArrayBuffer> {
  const response = await check(await fetch(`/api/geometry?${new URLSearchParams({ map })}`))
  const bytes = new Uint8Array(total)
  const reader = response.body!.getReader()
  let received = 0
  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    bytes.set(value, received)
    received += value.length
    onProgress(received / total)
  }
  return bytes.buffer
}

export function textureUrl(path: string): string {
  return `/api/texture?${new URLSearchParams({ path })}`
}

export interface PreviewOption {
  name: string
  value: boolean
  // the map's own setting, before the menu's changes
  mapValue: boolean
  description: string
}

export interface PreviewDecision {
  outcome: string
  rule: string | null
  to: string | null
  // the manual flow's group, the key a pick goes under, and the pick it ran with
  kind: string
  key: string
  picked: string | null
}

export interface PieceChoice {
  value: string
  label: string
}

export interface PreviewResult {
  options: PreviewOption[]
  // keyed by the block / item index in the map's own order (info.block, info.item)
  blocks: Record<string, PreviewDecision>
  items: Record<string, PreviewDecision>
  addedItems: number
  // data/ice-materials.jsonc: native material (or "skin:"/"top:"/"item:" + name, or "<block pattern>|name") -> importer material, "" = left out, "native" = kept
  swaps: Record<string, string>
  looks: Record<string, SceneMaterial>
  // id of the scene with the changed pieces drawn for real (fetchPreviewScene)
  addedScene: string
  // originals that scene replaces (or that are gone): hidden in the view, by info.block / info.item
  hiddenBlocks: number[]
  hiddenItems: number[]
  // background builds finished before this result (fetchBuildStatus)
  builds: number
  milliseconds: number
  // per kind what a piece can be picked to become ("" = as the rules say), and the kinds' names
  choices: Record<string, PieceChoice[]>
  kindLabels: Record<string, string>
}

export interface PreviewAsk {
  settings: Record<string, boolean>
  // piece key -> choice, from the manual flow
  picks: Record<string, string>
}

export async function fetchPreview(map: string, ask: PreviewAsk): Promise<PreviewResult> {
  const set = Object.entries(ask.settings).map(([name, value]) => `${name}=${value}`).join(',')
  const query = new URLSearchParams({ map })
  if (set) query.set('set', set)
  return (await check(await fetch(`/api/preview?${query}`, { method: 'POST', body: JSON.stringify({ picks: ask.picks }) }))).json()
}

export async function fetchOptions(map: string): Promise<PreviewOption[]> {
  return (await check(await fetch(`/api/options?${new URLSearchParams({ map })}`))).json()
}

export async function fetchPreviewScene(id: string): Promise<{ manifest: SceneManifest; geometry: ArrayBuffer }> {
  const [manifest, geometry] = await Promise.all([
    check(await fetch(`/api/preview-scene?id=${id}`)).then((r) => r.json()),
    check(await fetch(`/api/preview-scene?id=${id}&part=geometry`)).then((r) => r.arrayBuffer()),
  ])
  return { manifest, geometry }
}

export interface BuildStatus {
  running: boolean
  done: number
  total: number
  builds: number
}

export async function fetchBuildStatus(): Promise<BuildStatus> {
  return (await check(await fetch('/api/preview-build'))).json()
}
