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

// folders under Maps ("/" separated) and where a conversion goes by default
export interface FolderList {
  folders: string[]
  progress: string
}

// the account a converted map can carry as author (local.jsonc "testrunAuthor"); null when none is set
export interface Author {
  login: string
  nickname: string
  zone: string
}

export async function fetchAuthor(): Promise<Author | null> {
  return (await check(await fetch('/api/author'))).json()
}

export async function saveAuthor(author: Author): Promise<Author | null> {
  return (await check(await fetch('/api/author', { method: 'POST', body: JSON.stringify(author) }))).json()
}

export interface ConvertRequest {
  settings: MapSettings
  name: string
  folder: string
  fakeAuthorTime: boolean
  meAsAuthor: boolean
  rebuildBlocks: boolean
  overwrite: boolean
}

// output: relative to Maps (or a full path outside it)
export interface ConvertOutcome {
  output: string
  name: string
  runId: string
  from: string
  summary: string
  problems: string[]
  failures: string[]
  missingFiles: number
}

// step -1: not started; step = stepNames.length: finished
export interface ConvertProgress {
  step: number
  done: number
  total: number
  detail: string | null
  percent: number
  stepNames: string[]
}

export interface ConvertJob {
  id: string
  map: string
  name: string
  state: 'running' | 'done' | 'failed'
  progress: ConvertProgress
  started: string
  ended: string | null
  outcome: ConvertOutcome | null
  error: string | null
}

// starts it in the background
export async function startConvert(map: string, request: ConvertRequest): Promise<ConvertJob> {
  const query = new URLSearchParams({ map })
  return (await check(await fetch(`/api/convert?${query}`, { method: 'POST', body: JSON.stringify(request) }))).json()
}

// the running or last finished one; null when there's none
export async function fetchConvertJob(): Promise<ConvertJob | null> {
  return (await check(await fetch('/api/convert'))).json()
}

export async function dismissConvert(id: string): Promise<void> {
  await check(await fetch(`/api/convert-dismiss?${new URLSearchParams({ id })}`, { method: 'POST', body: '' }))
}

// what a conversion names the map by default (maps.jsonc names included)
export async function fetchOutputName(map: string): Promise<string> {
  return (await check(await fetch(`/api/output-name?${new URLSearchParams({ map })}`))).json()
}

// whether <folder>/<name>.Map.Gbx is already there under Maps
export async function targetExists(folder: string, name: string): Promise<boolean> {
  return (await check(await fetch(`/api/exists?${new URLSearchParams({ folder, name })}`))).json()
}

export async function fetchFolders(): Promise<FolderList> {
  return (await check(await fetch('/api/folders'))).json()
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
  // the converter's default (maps.jsonc "options")
  mapValue: boolean
  description: string
}

// one map's settings, shaped like its maps.jsonc entry; fields the page doesn't know are kept as they are
export type MapSettings = Record<string, unknown> & {
  options?: Record<string, unknown>
  pieces?: Record<string, unknown>
}

export interface RunInfo {
  id: string
  time: string
  kind: string
  commit: string | null
  output: string
  outputName: string
  // settings fields that didn't read when it ran
  leftOut: number
  settings: MapSettings
}

// what a map opens with: the settings a convert would use now, and where they came from
export interface MapState {
  // the original's UID: drafts are kept per family
  family: string
  familyName: string
  uids: number
  settings: MapSettings
  // "maps.jsonc" or "draft of …"
  from: string
  // the run (or "maps.jsonc") the draft started from
  draftFrom: string | null
  // the original's maps.jsonc entry
  seed: MapSettings
  // every option with its default and description
  defaults: PreviewOption[]
  // newest first
  runs: RunInfo[]
  unreadable: string[]
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
  // settings fields left out because they don't read; picks for pieces this map doesn't have
  problems: string[]
  stalePicks: string[]
}

export async function fetchPreview(map: string, settings: MapSettings): Promise<PreviewResult> {
  const query = new URLSearchParams({ map })
  return (await check(await fetch(`/api/preview?${query}`, { method: 'POST', body: JSON.stringify({ settings }) }))).json()
}

export async function fetchMapState(map: string): Promise<MapState> {
  return (await check(await fetch(`/api/settings?${new URLSearchParams({ map })}`))).json()
}

// null: drop the draft, back to maps.jsonc
export async function saveDraft(map: string, settings: MapSettings | null, from: string | null): Promise<void> {
  await check(await fetch(`/api/draft?${new URLSearchParams({ map })}`, { method: 'POST', body: JSON.stringify({ settings, from }) }))
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
