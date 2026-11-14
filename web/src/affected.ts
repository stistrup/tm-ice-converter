import { computed, ref } from 'vue'
import { fetchBuildStatus, type BuildStatus, type PreviewDecision } from './api'
import { useHistoryStore } from './stores/history'
import { useMapStore } from './stores/map'
import { usePreviewStore } from './stores/preview'

// "" = every group; else the group and what's under it
export const inGroup = (group: string | null, path: string) => group !== null && (path === '' || group === path || group.startsWith(`${path}/`))

// "Select affected blocks", and Deselect until the selection changes; keys: the selected pieces, for applying to
export const useAffected = (takes: () => (decision: PreviewDecision) => boolean) => {
  const store = useMapStore()
  const preview = usePreviewStore()
  const history = useHistoryStore()

  const affected = computed(() => {
    const test = takes()
    return (preview.decisions ?? []).flatMap((d, i) => (d && test(d) ? [i] : []))
  })
  const made = ref<number[] | null>(null)
  const selectedByUs = computed(() => made.value !== null && store.selection === made.value)

  const toggle = () =>
    history.change(() => {
      store.selection = selectedByUs.value ? [] : affected.value
      made.value = store.selection.length ? store.selection : null
    })

  const keys = () => store.selection.map((i) => preview.decisions?.[i]?.key).filter((k): k is string => !!k)

  return { affected, selectedByUs, toggle, keys }
}

// applying takes a while when pieces need building first: the status says how far
export const useApplying = () => {
  const applying = ref(false)
  const building = ref<BuildStatus | null>(null)
  const status = computed(() =>
    !applying.value ? '' : building.value?.running ? `Building pieces ${building.value.done} of ${building.value.total}…` : 'Applying…',
  )

  const run = async (apply: () => Promise<unknown>) => {
    if (applying.value) return
    applying.value = true
    const poll = setInterval(async () => {
      building.value = await fetchBuildStatus().catch(() => null)
    }, 1000)
    try {
      await apply()
    } finally {
      clearInterval(poll)
      building.value = null
      applying.value = false
    }
  }

  return { applying, status, run }
}
