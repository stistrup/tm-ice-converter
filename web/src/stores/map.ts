import { defineStore } from 'pinia'
import { computed, ref, shallowRef } from 'vue'
import { fetchGeometry, fetchMaps, fetchScene, type InstanceInfo, type MapEntry, type SceneManifest } from '../api'
import { categoryOf, type Category } from '../viewer/categories'

export interface LoadedScene {
  manifest: SceneManifest
  geometry: ArrayBuffer
}

export const useMapStore = defineStore('map', () => {
  const maps = ref<MapEntry[]>([])
  const mapsError = ref('')
  const currentMap = ref<MapEntry | null>(null)
  const scene = shallowRef<LoadedScene | null>(null)
  const status = ref('')
  const loadError = ref('')
  const hovered = ref<number | null>(null)
  const selected = ref<number[]>([])
  const hidden = ref<Category[]>(['terrain'])
  const focusRequest = ref(0)

  let loadId = 0

  // instances, then placeholders, then boxes: the order MapScene numbers them in
  const infos = computed<InstanceInfo[]>(() => {
    const manifest = scene.value?.manifest
    if (!manifest) return []
    return [...manifest.instances, ...manifest.placeholders, ...manifest.boxes].map((i) => i.info)
  })

  const pieceNames = computed(() => {
    const counts = new Map<string, { name: string; category: Category; indices: number[] }>()
    infos.value.forEach((info, index) => {
      const entry = counts.get(info.name)
      if (entry) entry.indices.push(index)
      else counts.set(info.name, { name: info.name, category: categoryOf(info), indices: [index] })
    })
    return [...counts.values()].sort((a, b) => b.indices.length - a.indices.length)
  })

  async function loadMaps() {
    try {
      maps.value = await fetchMaps()
    } catch (error) {
      mapsError.value = String(error)
    }
  }

  async function openMap(map: MapEntry, rebuild = false) {
    const id = ++loadId
    currentMap.value = map
    scene.value = null
    selected.value = []
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
    focusRequest,
    infos,
    pieceNames,
    loadMaps,
    openMap,
    toggleCategory,
  }
})
