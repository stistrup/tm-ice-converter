import { acceptHMRUpdate, defineStore } from 'pinia'
import { ref } from 'vue'
import { dismissConvert, fetchConvertJob, startConvert, type ConvertJob, type ConvertRequest } from '../api'
import { useMapStore } from './map'
import { usePreviewStore } from './preview'

// the conversion running in the backend: shown in the dialog or as the bar in the corner; found again after a reload
export const useConvertStore = defineStore('convert', () => {
  const job = ref<ConvertJob | null>(null)
  // the dialog shows it; else the bar does
  const inDialog = ref(false)
  let timer: ReturnType<typeof setTimeout> | undefined

  async function poll() {
    clearTimeout(timer)
    let now: ConvertJob | null
    try {
      now = await fetchConvertJob()
    } catch {
      timer = setTimeout(poll, 2000)
      return
    }
    const finished = job.value?.state === 'running' && now?.id === job.value.id && now.state !== 'running'
    job.value = now
    if (finished && now?.state === 'done') {
      void useMapStore().loadMaps()
      void usePreviewStore().refreshRuns()
    }
    if (now?.state === 'running') timer = setTimeout(poll, 400)
  }

  async function start(map: string, request: ConvertRequest) {
    job.value = await startConvert(map, request)
    inDialog.value = true
    void poll()
  }

  // a finished one let go
  async function dismiss() {
    const id = job.value?.id
    job.value = null
    inDialog.value = false
    if (id) await dismissConvert(id).catch(() => undefined)
  }

  return { job, inDialog, poll, start, dismiss }
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(useConvertStore, import.meta.hot))
