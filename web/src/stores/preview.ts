import { acceptHMRUpdate, defineStore } from 'pinia'
import { computed, ref, shallowRef, watch } from 'vue'
import {
  fetchBuildStatus,
  fetchMapState,
  fetchPieceKeys,
  normalKey,
  fetchPreview,
  fetchPreviewScene,
  applyGroup as postGroup,
  applyPreset as postPreset,
  editSettings,
  redoSettings,
  revertSettings,
  saveSettings,
  undoSettings,
  type BuildStatus,
  type DocumentState,
  type MapSettings,
  type MapState,
  type PieceKeys,
  type PreviewDecision,
  type PreviewOption,
  type PreviewResult,
  type SceneManifest,
} from '../api'
import { remembered } from '../storage'
import { useMapStore } from './map'

const OPEN_KEY = 'preview-open'

const clone = (settings: MapSettings): MapSettings => JSON.parse(JSON.stringify(settings))

// the open map's settings (a mirror of its working copy in the backend) and the converter's decisions with them; nothing is built
export const usePreviewStore = defineStore('preview', () => {
  const map = useMapStore()
  // the panel is always there; folding only hides it
  const open = remembered(OPEN_KEY, true)
  // where the map's settings stand: saved or defaults
  const state = ref<MapState | null>(null)
  // what the menu edits and the preview runs with, shaped like settings/maps/<uid>.json
  const working = ref<MapSettings>({})
  // the working copy against the saved settings
  const dirty = ref(false)
  const canUndo = ref(false)
  const canRedo = ref(false)
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

  const targetsOf = (settings: MapSettings): Record<string, string> => {
    const pieces = settings.pieces
    if (!pieces || typeof pieces !== 'object') return {}
    return Object.fromEntries(Object.entries(pieces).filter((e): e is [string, string] => typeof e[1] === 'string'))
  }

  // per piece key, from the manual flow
  const picks = computed(() => targetsOf(working.value))

  const keys = shallowRef<PieceKeys | null>(null)
  const loadKeys = async () => {
    const current = map.currentMap
    keys.value = null
    if (!current) return
    const loaded = await fetchPieceKeys(current.path).catch(() => null)
    if (map.currentMap === current) keys.value = loaded
  }

  // while a run works: the pieces whose targets it is changing (before the first result, every target), by scene index
  const waiting = computed(() => {
    const out = new Set<number>()
    const k = keys.value
    if (!loading.value || showOriginal.value || !k) return out
    const before = result.value ? targetsOf(JSON.parse(ranWith.value)) : {}
    const now = picks.value
    const changed = new Set([...Object.keys(before), ...Object.keys(now)].filter((key) => before[key] !== now[key]).map(normalKey))
    if (!changed.size) return out
    map.infos.forEach((info, i) => {
      const hit = typeof info.block === 'number' ? changed.has(k.blocks[info.block]) || changed.has(k.plainBlocks[info.block]) : typeof info.item === 'number' && changed.has(k.items[info.item])
      if (hit) out.add(i)
    })
    return out
  })

  // the working settings as they are, or (after builds) what the shown result came from
  const run = async (settings: MapSettings = working.value) => {
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
  const followBuilds = async (id: number) => {
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

  // the backend keeps the working copy; calls go one after another, each answer says where it stands
  let queue: Promise<unknown> = Promise.resolve()
  const documentCall = (call: (map: string) => Promise<DocumentState>, adopt: boolean) => {
    const current = map.currentMap
    if (!current) return Promise.resolve()
    const next = queue.then(async () => {
      try {
        const answer = await call(current.path)
        if (map.currentMap !== current) return
        dirty.value = answer.dirty
        canUndo.value = answer.canUndo
        canRedo.value = answer.canRedo
        if (adopt) working.value = clone(answer.settings)
      } catch (e) {
        error.value = `Settings not kept: ${e instanceof Error ? e.message : String(e)}`
      }
    })
    queue = next
    return next
  }

  // the settings as they are now: one undoable step
  const commit = () => documentCall((path) => editSettings(path, clone(working.value)), false)
  const undo = () => documentCall(undoSettings, true)
  const redo = () => documentCall(redoSettings, true)
  const save = () => documentCall(saveSettings, false)
  const revert = () => documentCall(revertSettings, true)
  const applyPreset = (name: string, only: string[] | null) => documentCall((path) => postPreset(path, name, only), true)
  const applyGroup = (group: string, method: string, only: string[] | null) => documentCall((path) => postGroup(path, group, method, only), true)

  const loadState = async () => {
    const current = map.currentMap
    state.value = null
    if (!current) return
    try {
      const loaded = await fetchMapState(current.path)
      if (map.currentMap !== current) return
      state.value = loaded
      working.value = clone(loaded.settings)
      dirty.value = loaded.dirty
      canUndo.value = loaded.canUndo
      canRedo.value = loaded.canRedo
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
    }
  }

  // folding only hides the panel; the preview stays drawn
  const show = () => {
    open.value = true
  }

  const close = () => {
    open.value = false
  }

  const clear = () => {
    runId++
    loading.value = false
    result.value = null
    changed.value = null
    building.value = null
    ranWith.value = ''
  }

  watch(
    () => map.currentMap,
    () => {
      working.value = {}
      dirty.value = canUndo.value = canRedo.value = false
      error.value = ''
      clear()
      void loadState()
      void loadKeys()
    },
    { immediate: true },
  )

  const defaultOf = (name: string) => options.value.find((o) => o.name === name)?.mapValue ?? false

  const optionIn = (settings: MapSettings | undefined, name: string): boolean => {
    const value = settings?.options?.[name]
    return typeof value === 'boolean' ? value : defaultOf(name)
  }

  // the box as the working settings have it
  const valueOf = (name: string) => optionIn(working.value, name)

  // a default is left out, so the settings only say what's special about the map
  const set = (name: string, value: boolean) => {
    const own = { ...(working.value.options ?? {}) }
    if (value === defaultOf(name)) delete own[name]
    else own[name] = value
    const { options: _, ...rest } = working.value
    working.value = Object.keys(own).length ? { ...rest, options: own } : rest
  }

  // off: only the pieces picked by hand convert


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
  const pick = (keys: string[], choice: string) => {
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
    dirty,
    canUndo,
    canRedo,
    commit,
    undo,
    redo,
    save,
    revert,
    applyPreset,
    applyGroup,
    picks,
    pick,
    building,
    waiting,
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
    valueOf,
  }
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(usePreviewStore, import.meta.hot))
