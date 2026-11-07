import { acceptHMRUpdate, defineStore } from 'pinia'
import { ref } from 'vue'
import { fetchSetup, setSetupPath, setupAction, type SetupStatus } from '../api'

// the first-run checklist: shown until everything is ready, or opened again from the status bar
export const useSetupStore = defineStore('setup', () => {
  const status = ref<SetupStatus | null>(null)
  const shown = ref(false)
  const error = ref('')
  // the converter gone for good, not one missed check
  const lost = ref(false)
  let misses = 0
  let timer: ReturnType<typeof setTimeout> | undefined

  async function load() {
    clearTimeout(timer)
    try {
      status.value = await fetchSetup()
      misses = 0
      lost.value = false
      if (!status.value.ready) shown.value = true
    } catch {
      lost.value = ++misses >= 3
    }
    // the game starting, closing or extracting changes it
    if (shown.value) timer = setTimeout(load, 2000)
  }

  async function run(change: () => Promise<SetupStatus>) {
    error.value = ''
    try {
      status.value = await change()
    } catch (e) {
      error.value = (e as Error).message
    }
  }

  const setPath = (name: string, value: string) => run(() => setSetupPath(name, value))
  const act = (id: string) => run(() => setupAction(id))

  function open() {
    shown.value = true
    void load()
  }

  function close() {
    shown.value = false
    clearTimeout(timer)
  }

  return { status, shown, error, lost, load, setPath, act, open, close }
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(useSetupStore, import.meta.hot))
