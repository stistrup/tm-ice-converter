import { acceptHMRUpdate, defineStore } from 'pinia'
import { computed, ref, shallowRef, watch } from 'vue'
import { fetchGeometry, fetchMaps, fetchScene, type InstanceInfo, type MapEntry, type SceneManifest } from '../api'
import { categoryOf, type Category } from '../viewer/categories'
import type { CameraState, MapScene } from '../viewer/MapScene'

export interface LoadedScene {
  manifest: SceneManifest
  geometry: ArrayBuffer
}

const MAP_KEY = 'current-map'
const SHOWN_KEY = 'maps-shown'
const HIDDEN_KEY = 'hidden-categories'
const VIEW_KEY = 'view:'
const CHOSEN_KEY = 'chosen-maps'

// per map file: where the camera was and what was selected; only while the file is unchanged (indices follow the file)
export interface ViewMemory {
  modified: string
  selection: number[]
  camera: CameraState | null
}

function readJson<T>(key: string, fallback: T): T {
  try {
    const text = localStorage.getItem(key)
    return text === null ? fallback : (JSON.parse(text) as T)
  } catch {
    return fallback
  }
}

function writeJson(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value))
  } catch {
    // only a convenience
  }
}

function readShown(): boolean {
  try {
    return localStorage.getItem(SHOWN_KEY) !== 'false'
  } catch {
    return true
  }
}

export const useMapStore = defineStore('map', () => {
  const maps = ref<MapEntry[]>([])
  const mapsError = ref('')
  const currentMap = ref<MapEntry | null>(null)
  const scene = shallowRef<LoadedScene | null>(null)
  const status = ref('')
  const loadError = ref('')
  const hovered = ref<number | null>(null)
  const selection = ref<number[]>([])
  // map paths ticked in the list, for converting several at once
  const chosenMaps = ref(new Set<string>(readJson<string[]>(CHOSEN_KEY, [])))
  watch(chosenMaps, (chosen) => writeJson(CHOSEN_KEY, [...chosen]), { deep: true })
  // the last picked piece, the one the properties panel shows
  const selected = computed<number | null>({
    get: () => selection.value.at(-1) ?? null,
    set: (index) => (selection.value = index === null ? [] : [index]),
  })

  // click: just this one (again: none); shift-click: add or remove it
  function pick(index: number | null, add: boolean) {
    if (add) {
      if (index === null) return
      selection.value = selection.value.includes(index)
        ? selection.value.filter((i) => i !== index)
        : [...selection.value, index]
    } else {
      const only = selection.value.length === 1 && selection.value[0] === index
      selected.value = only ? null : index
    }
  }
  function paint(index: number, add: boolean) {
    const has = selection.value.includes(index)
    if (add && !has) selection.value = [...selection.value, index]
    else if (!add && has) selection.value = selection.value.filter((i) => i !== index)
  }
  const view = shallowRef<MapScene | null>(null)
  const textureLoads = ref(0)
  const cameraMoves = ref(0)
  const hidden = ref<Category[]>(readJson<Category[]>(HIDDEN_KEY, []))
  watch(hidden, (value) => writeJson(HIDDEN_KEY, value))

  function viewMemory(map: MapEntry): ViewMemory | null {
    const memory = readJson<ViewMemory | null>(VIEW_KEY + map.path, null)
    return memory?.modified === map.modified ? memory : null
  }

  function rememberView(patch: Partial<ViewMemory>) {
    const map = currentMap.value
    if (!map || !scene.value) return
    const memory = viewMemory(map) ?? { modified: map.modified, selection: [], camera: null }
    writeJson(VIEW_KEY + map.path, { ...memory, ...patch, modified: map.modified })
  }

  watch(selection, (value) => rememberView({ selection: value }))
  const mapsShown = ref(readShown())

  watch(mapsShown, (value) => {
    try {
      localStorage.setItem(SHOWN_KEY, String(value))
    } catch {
      // only a convenience
    }
  })

  let loadId = 0

  function rememberMap(path: string) {
    try {
      localStorage.setItem(MAP_KEY, path)
    } catch {
      // only a convenience
    }
  }

  function forgetMap() {
    try {
      localStorage.removeItem(MAP_KEY)
    } catch {
      // only a convenience
    }
  }

  function rememberedMap(): string | null {
    try {
      return localStorage.getItem(MAP_KEY)
    } catch {
      return null
    }
  }

  // instances, then placeholders, then boxes: the order MapScene numbers them in
  const infos = computed<InstanceInfo[]>(() => {
    const manifest = scene.value?.manifest
    if (!manifest) return []
    return [...manifest.instances, ...manifest.placeholders, ...manifest.boxes].map((i) => i.info)
  })

  const categoryCounts = computed(() => {
    const counts = new Map<Category, number>()
    for (const info of infos.value) {
      const category = categoryOf(info)
      counts.set(category, (counts.get(category) ?? 0) + 1)
    }
    return counts
  })

  async function loadMaps() {
    try {
      maps.value = await fetchMaps()
      const remembered = maps.value.find((map) => map.path === rememberedMap())
      if (remembered && !currentMap.value) void openMap(remembered)
    } catch (error) {
      mapsError.value = String(error)
    }
  }

  async function openMap(map: MapEntry, rebuild = false) {
    const id = ++loadId
    currentMap.value = map
    rememberMap(map.path)
    scene.value = null
    selected.value = null
    hovered.value = null
    loadError.value = ''
    try {
      status.value = rebuild ? 'Rebuilding scene…' : 'Reading the map…'
      const manifest = await fetchScene(map.path, rebuild)
      if (id !== loadId) return
      status.value = 'Loading geometry…'
      const geometry = await fetchGeometry(map.path, manifest.geometryBytes, (fraction) => {
        if (id === loadId) status.value = `Loading geometry… ${Math.round(fraction * 100)}%`
      })
      if (id !== loadId) return
      scene.value = { manifest, geometry }
      selection.value = viewMemory(map)?.selection.filter((i) => i < infos.value.length) ?? []
      status.value = ''
    } catch (error) {
      if (id !== loadId) return
      status.value = ''
      loadError.value = error instanceof Error ? error.message : String(error)
    }
  }

  function closeMap() {
    loadId++
    currentMap.value = null
    forgetMap()
    scene.value = null
    selected.value = null
    hovered.value = null
    status.value = ''
    loadError.value = ''
  }

  function toggleCategory(category: Category) {
    hidden.value = hidden.value.includes(category)
      ? hidden.value.filter((c) => c !== category)
      : [...hidden.value, category]
  }

  return {
    maps,
    mapsError,
    currentMap,
    closeMap,
    scene,
    status,
    loadError,
    hovered,
    selected,
    selection,
    chosenMaps,
    pick,
    paint,
    hidden,
    mapsShown,
    infos,
    categoryCounts,
    view,
    textureLoads,
    cameraMoves,
    loadMaps,
    openMap,
    toggleCategory,
    viewMemory,
    rememberView,
  }
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(useMapStore, import.meta.hot))
