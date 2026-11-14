import { acceptHMRUpdate, defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'
import { useMapStore } from './map'
import { usePreviewStore } from './preview'

// a step: the selection before and after, and whether the settings changed (the backend holds those steps)
interface Step {
  before: number[]
  after: number[]
  settings: boolean
}

// undo/redo over what the user changes on the open map: the selection here, the settings in the map's working copy
export const useHistoryStore = defineStore('history', () => {
  const map = useMapStore()
  const preview = usePreviewStore()
  const past = ref<Step[]>([])
  const future = ref<Step[]>([])

  // key order aside, as the backend compares
  const canonical = (value: unknown): string =>
    JSON.stringify(value, (_k, v) => (v && typeof v === 'object' && !Array.isArray(v) ? Object.fromEntries(Object.entries(v).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))) : v))
  const sameSelection = (a: number[], b: number[]) => a.length === b.length && a.every((v, i) => v === b[i])

  const push = (step: Step) => {
    past.value = [...past.value.slice(-199), step]
    future.value = []
  }

  // one undo step per change that did something
  const change = (fn: () => void) => {
    const before = map.selection
    const settingsBefore = canonical(preview.working)
    fn()
    const settings = canonical(preview.working) !== settingsBefore
    if (!settings && sameSelection(before, map.selection)) return
    if (settings) void preview.commit()
    push({ before, after: map.selection, settings })
  }

  // a step of its own
  const revert = () => {
    if (!preview.dirty) return
    void preview.revert()
    push({ before: map.selection, after: map.selection, settings: true })
  }

  // a preset's targets: one step
  const applied = async (apply: () => Promise<unknown>) => {
    const selection = map.selection
    await apply()
    push({ before: selection, after: selection, settings: true })
  }
  const preset = (name: string, only: string[] | null) => applied(() => preview.applyPreset(name, only))

  // a shift-drag stroke is one step
  let strokeBefore: number[] | null = null
  const stroke = (phase: 'start' | 'end') => {
    if (phase === 'start') strokeBefore = map.selection
    else if (strokeBefore) {
      if (!sameSelection(strokeBefore, map.selection)) push({ before: strokeBefore, after: map.selection, settings: false })
      strokeBefore = null
    }
  }

  // with nothing of this session left, the working copy's own history (edits from before a reload)
  const undo = () => {
    const last = past.value.at(-1)
    if (!last) {
      if (preview.canUndo) void preview.undo()
      return
    }
    past.value = past.value.slice(0, -1)
    future.value = [...future.value, last]
    map.selection = last.before
    if (last.settings) void preview.undo()
  }

  const redo = () => {
    const next = future.value.at(-1)
    if (!next) {
      if (preview.canRedo) void preview.redo()
      return
    }
    future.value = future.value.slice(0, -1)
    past.value = [...past.value, next]
    map.selection = next.after
    if (next.settings) void preview.redo()
  }

  const canUndo = computed(() => past.value.length > 0 || preview.canUndo)
  const canRedo = computed(() => future.value.length > 0 || preview.canRedo)

  watch(
    () => map.currentMap,
    () => {
      past.value = []
      future.value = []
    },
  )

  return { past, future, canUndo, canRedo, change, revert, preset, stroke, undo, redo }
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(useHistoryStore, import.meta.hot))
