import { acceptHMRUpdate, defineStore } from 'pinia'
import { computed, ref, shallowRef, watch } from 'vue'
import { type PreviewAsk, fetchBuildStatus, fetchOptions, fetchPreview, fetchPreviewScene, type BuildStatus, type SceneManifest, type PreviewDecision, type PreviewOption, type PreviewResult } from '../api'
import { useMapStore } from './map'

const OPEN_KEY = 'preview-open'

function readOpen(): boolean {
  try {
    return localStorage.getItem(OPEN_KEY) !== 'false'
  } catch {
    return true
  }
}

// the converter's decisions for the open map with the menu's options; nothing is built
export const usePreviewStore = defineStore('preview', () => {
  const map = useMapStore()
  // the panel is always there; folding only hides it
  const open = ref(readOpen())
  // only options changed from the map's own setting
  const settings = ref<Record<string, boolean>>({})
  const result = shallowRef<PreviewResult | null>(null)
  const loading = ref(false)
  const error = ref('')
  // the settings the shown result came from
  const ranWith = ref('')
  // bumped when the view redraws the preview (the properties picture follows)
  const lookVersion = ref(0)
  // the changed pieces as the conversion makes them, where their meshes exist
  const changed = shallowRef<{ manifest: SceneManifest; geometry: ArrayBuffer } | null>(null)
  let runId = 0

  // per piece key, from the manual flow; sent with every preview
  const picks = ref<Record<string, string>>({})

  const asking = (): PreviewAsk => ({ settings: settings.value, picks: picks.value })

  // the boxes and picks as they are, or (after builds) what the shown result came from
  async function run(asked: PreviewAsk = asking()) {
    const current = map.currentMap
    if (!current) return
    const id = ++runId
    loading.value = true
    error.value = ''
    try {
      const answer = await fetchPreview(current.path, asked)
      const scene = await fetchPreviewScene(answer.addedScene)
      if (id === runId) {
        changed.value = scene
        result.value = answer
        ranWith.value = JSON.stringify(asked)
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

  // the map's own option values, loaded without working anything out
  const options = ref<PreviewOption[]>([])

  async function loadOptions() {
    const current = map.currentMap
    options.value = []
    if (!current) return
    try {
      const list = await fetchOptions(current.path)
      if (map.currentMap === current) options.value = list
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
      settings.value = {}
      picks.value = {}
      error.value = ''
      clear()
      void loadOptions()
    },
    { immediate: true },
  )


  function mapValueOf(name: string) {
    return options.value.find((o) => o.name === name)?.mapValue ?? false
  }

  function set(name: string, value: boolean) {
    const next = { ...settings.value }
    if (mapValueOf(name) === value) delete next[name]
    else next[name] = value
    settings.value = next
  }

  // the boxes as ticked, before Preview runs them
  function valueOf(name: string): boolean {
    return settings.value[name] ?? mapValueOf(name)
  }

  const stale = computed(() => JSON.stringify(asking()) !== ranWith.value)

  // "" = back to the rules; the preview follows right away
  function pick(keys: string[], choice: string) {
    const next = { ...picks.value }
    for (const key of keys) {
      if (choice) next[key] = choice
      else delete next[key]
    }
    picks.value = next
    void run()
  }


  // one per scene piece, in store.infos order
  const decisions = computed<(PreviewDecision | undefined)[] | null>(() => {
    const r = result.value
    if (!r) return null
    return map.infos.map((info) =>
      typeof info.block === 'number' ? r.blocks[info.block] : typeof info.item === 'number' ? r.items[info.item] : undefined,
    )
  })

  return { picks, pick, building, changed, lookVersion, open, options, settings, result, loading, error, stale, decisions, run, show, close, set, valueOf }
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(usePreviewStore, import.meta.hot))
