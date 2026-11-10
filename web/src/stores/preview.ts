import { acceptHMRUpdate, defineStore } from 'pinia'
import { computed, ref, shallowRef, watch } from 'vue'
import {
  fetchBuildStatus,
  fetchMapState,
  fetchPreview,
  fetchPreviewScene,
  saveSettings,
  type BuildStatus,
  type MapSettings,
  type MapState,
  type PreviewDecision,
  type PreviewOption,
  type PreviewResult,
  type SceneManifest,
} from '../api'
import { useMapStore } from './map'

const OPEN_KEY = 'preview-open'

function readOpen(): boolean {
  try {
    return localStorage.getItem(OPEN_KEY) !== 'false'
  } catch {
    return true
  }
}

const clone = (settings: MapSettings): MapSettings => JSON.parse(JSON.stringify(settings))

// the open map's settings (saved for the map as they change) and the converter's decisions with them; nothing is built
export const usePreviewStore = defineStore('preview', () => {
  const map = useMapStore()
  // the panel is always there; folding only hides it
  const open = ref(readOpen())
  // where the map's settings stand: saved or defaults
  const state = ref<MapState | null>(null)
  // what the menu edits and the preview runs with, shaped like settings/maps/<uid>.json
  const working = ref<MapSettings>({})
  const result = shallowRef<PreviewResult | null>(null)
  const loading = ref(false)
  const error = ref('')
  // the settings the shown result came from
  const ranWith = ref('')
  // bumped when the view redraws the preview (the properties picture follows)
  const lookVersion = ref(0)
  // the view shows the map as it is, the preview kept
  const showOriginal = ref(false)
  // the changed pieces as the conversion makes them, where their meshes exist
  const changed = shallowRef<{ manifest: SceneManifest; geometry: ArrayBuffer } | null>(null)
  let runId = 0

  const options = computed<PreviewOption[]>(() => state.value?.defaults ?? [])

  // per piece key, from the manual flow
  const picks = computed<Record<string, string>>(() => {
    const pieces = working.value.pieces
    if (!pieces || typeof pieces !== 'object') return {}
    // snowbottom: older saves, now plain snow
    return Object.fromEntries(
      Object.entries(pieces)
        .filter((e): e is [string, string] => typeof e[1] === 'string')
        .map(([key, value]) => [key, value === 'snowbottom' ? 'snow' : value]),
    )
  })

  // the working settings as they are, or (after builds) what the shown result came from
  async function run(settings: MapSettings = working.value) {
    const current = map.currentMap
    if (!current) return
    const id = ++runId
    loading.value = true
    error.value = ''
    try {
      const answer = await fetchPreview(current.path, settings)
      const scene = await fetchPreviewScene(answer.addedScene)
      if (id === runId) {
        changed.value = scene
        result.value = answer
        ranWith.value = JSON.stringify(settings)
        void followBuilds(id)
      }
    } catch (e) {
      if (id === runId) error.value = e instanceof Error ? e.message : String(e)
    } finally {
      if (id === runId) loading.value = false
    }
  }

  // missing pieces build in the background; when they're in, the same preview runs again and shows them for real
  const building = ref<BuildStatus | null>(null)
  async function followBuilds(id: number) {
    for (;;) {
      await new Promise((resolve) => setTimeout(resolve, 1000))
      if (id !== runId || !result.value) return
      let status: BuildStatus
      try {
        status = await fetchBuildStatus()
      } catch {
        return
      }
      if (id !== runId) return
      building.value = status.running ? status : null
      if (status.running) continue
      if (status.builds > result.value.builds) void run(JSON.parse(ranWith.value))
      return
    }
  }

  // saved only while the settings differ from the defaults
  let saved = ''
  let saveTimer: ReturnType<typeof setTimeout> | undefined
  function scheduleSave() {
    const current = map.currentMap
    const seed = state.value?.seed
    if (!current || !seed) return
    clearTimeout(saveTimer)
    saveTimer = setTimeout(async () => {
      const json = JSON.stringify(working.value)
      if (json === saved) return
      const same = json === JSON.stringify(seed)
      try {
        await saveSettings(current.path, same ? null : working.value)
        saved = json
      } catch (e) {
        error.value = `Settings not saved: ${e instanceof Error ? e.message : String(e)}`
      }
    }, 400)
  }

  watch(working, scheduleSave, { deep: true })

  async function loadState() {
    const current = map.currentMap
    state.value = null
    if (!current) return
    try {
      const loaded = await fetchMapState(current.path)
      if (map.currentMap !== current) return
      state.value = loaded
      saved = JSON.stringify(loaded.settings)
      working.value = clone(loaded.settings)
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
    }
  }

  // folding only hides the panel; the preview stays drawn
  function show() {
    open.value = true
  }

  function close() {
    open.value = false
  }

  function clear() {
    runId++
    loading.value = false
    result.value = null
    changed.value = null
    building.value = null
    ranWith.value = ''
  }

  watch(open, (value) => {
    try {
      localStorage.setItem(OPEN_KEY, String(value))
    } catch {
      // only a convenience
    }
  })

  watch(
    () => map.currentMap,
    () => {
      clearTimeout(saveTimer)
      working.value = {}
      error.value = ''
      clear()
      void loadState()
    },
    { immediate: true },
  )

  const defaultOf = (name: string) => options.value.find((o) => o.name === name)?.mapValue ?? false

  function optionIn(settings: MapSettings | undefined, name: string): boolean {
    const value = settings?.options?.[name]
    return typeof value === 'boolean' ? value : defaultOf(name)
  }

  // the box as the working settings have it
  const valueOf = (name: string) => optionIn(working.value, name)

  // a default is left out, so the settings only say what's special about the map
  function set(name: string, value: boolean) {
    const own = { ...(working.value.options ?? {}) }
    if (value === defaultOf(name)) delete own[name]
    else own[name] = value
    const { options: _, ...rest } = working.value
    working.value = Object.keys(own).length ? { ...rest, options: own } : rest
  }

  // off: only the pieces picked by hand convert
  const presets = computed(() => working.value.presets !== false)

  function setPresets(on: boolean) {
    const { presets: _, ...rest } = working.value
    working.value = on ? rest : { ...rest, presets: false }
  }

  const stale = computed(() => JSON.stringify(working.value) !== ranWith.value)

  // while the preview is shown it follows the settings; the first run on a map is the one the server warmed up
  let asked = ''
  let runTimer: ReturnType<typeof setTimeout> | undefined
  watch(
    [working, showOriginal, state],
    () => {
      clearTimeout(runTimer)
      if (showOriginal.value || !state.value) return
      runTimer = setTimeout(() => {
        const json = JSON.stringify(working.value)
        if (json === asked && (loading.value || json === ranWith.value)) return
        asked = json
        void run()
      }, 250)
    },
    { deep: true },
  )

  // "" = back to the rules; the preview follows right away
  function pick(keys: string[], choice: string) {
    const pieces: Record<string, unknown> = { ...(working.value.pieces ?? {}) }
    for (const key of keys) {
      if (choice) pieces[key] = choice
      else delete pieces[key]
    }
    const { pieces: _, ...rest } = working.value
    working.value = Object.keys(pieces).length ? { ...rest, pieces } : rest
  }

  // one per scene piece, in store.infos order
  const decisions = computed<(PreviewDecision | undefined)[] | null>(() => {
    const r = result.value
    if (!r) return null
    return map.infos.map((info) =>
      typeof info.block === 'number' ? r.blocks[info.block] : typeof info.item === 'number' ? r.items[info.item] : undefined,
    )
  })

  return {
    state,
    working,
    picks,
    pick,
    building,
    changed,
    lookVersion,
    showOriginal,
    open,
    options,
    result,
    loading,
    error,
    stale,
    decisions,
    run,
    show,
    close,
    set,
    presets,
    setPresets,
    valueOf,
  }
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(usePreviewStore, import.meta.hot))
