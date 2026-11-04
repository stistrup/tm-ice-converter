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
}

export interface ScenePiece {
  material: number
  offset: number
  vertices: number
  indices: number
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
