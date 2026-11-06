import { acceptHMRUpdate, defineStore } from 'pinia'
import { ref, watch } from 'vue'
import type { MapSettings } from '../api'
import { useMapStore } from './map'
import { usePreviewStore } from './preview'

interface Snapshot {
  selection: number[]
  settings: MapSettings
  from: string | null
}

// undo/redo over what the user changes on the open map: the selection and the conversion options
export const useHistoryStore = defineStore('history', () => {
  const map = useMapStore()
  const preview = usePreviewStore()
  const past = ref<Snapshot[]>([])
  const future = ref<Snapshot[]>([])

  const now = (): Snapshot => ({ selection: map.selection, settings: preview.working, from: preview.from })

  function restore(snapshot: Snapshot) {
    map.selection = snapshot.selection
    // the preview follows the settings by itself
    preview.working = snapshot.settings
    preview.from = snapshot.from
  }

  const same = (a: Snapshot, b: Snapshot) => JSON.stringify(a) === JSON.stringify(b)

  // one undo step per change that did something
  function change(fn: () => void) {
    const before = now()
    fn()
    commit(before)
  }

  function commit(before: Snapshot) {
    if (same(before, now())) return
    past.value = [...past.value.slice(-199), before]
    future.value = []
  }

  // a shift-drag stroke is one step
  let strokeBefore: Snapshot | null = null
  function stroke(phase: 'start' | 'end') {
    if (phase === 'start') strokeBefore = now()
    else if (strokeBefore) {
      commit(strokeBefore)
      strokeBefore = null
    }
  }

  function undo() {
    const last = past.value.at(-1)
    if (!last) return
    past.value = past.value.slice(0, -1)
    future.value = [...future.value, now()]
    restore(last)
  }

  function redo() {
    const next = future.value.at(-1)
    if (!next) return
    future.value = future.value.slice(0, -1)
    past.value = [...past.value, now()]
    restore(next)
  }

  watch(
    () => map.currentMap,
    () => {
      past.value = []
      future.value = []
    },
  )

  return { past, future, change, stroke, undo, redo }
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(useHistoryStore, import.meta.hot))
