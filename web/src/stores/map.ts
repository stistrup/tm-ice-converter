import { defineStore } from 'pinia'
import { computed, ref, shallowRef } from 'vue'
import { fetchGeometry, fetchMaps, fetchScene, type InstanceInfo, type MapEntry, type SceneManifest } from '../api'
import { categoryOf, type Category } from '../viewer/categories'
import type { MapScene } from '../viewer/MapScene'

export interface LoadedScene {
  manifest: SceneManifest
  geometry: ArrayBuffer
}

const MAP_KEY = 'current-map'

export const useMapStore = defineStore('map', () => {
  const maps = ref<MapEntry[]>([])
  const mapsError = ref('')
  const currentMap = ref<MapEntry | null>(null)
  const scene = shallowRef<LoadedScene | null>(null)
  const status = ref('')
  const loadError = ref('')
  const hovered = ref<number | null>(null)
  const selected = ref<number | null>(null)
  const view = shallowRef<MapScene | null>(null)
  const textureLoads = ref(0)
  const cameraMoves = ref(0)
  const hidden = ref<Category[]>([])

  let loadId = 0

  function rememberMap(path: string) {
    try {
      sessionStorage.setItem(MAP_KEY, path)
    } catch {
      // only a convenience
    }
  }

  function rememberedMap(): string | null {
    try {
      return sessionStorage.getItem(MAP_KEY)
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
      status.value = ''
    } catch (error) {
      if (id !== loadId) return
      status.value = ''
      loadError.value = error instanceof Error ? error.message : String(error)
    }
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
    scene,
    status,
    loadError,
    hovered,
    selected,
    hidden,
    infos,
    categoryCounts,
    view,
    textureLoads,
    cameraMoves,
    loadMaps,
    openMap,
    toggleCategory,
  }
})
