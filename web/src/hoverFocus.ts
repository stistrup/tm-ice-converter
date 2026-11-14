import type { CameraState } from './viewer/MapScene'
import { useHistoryStore } from './stores/history'
import { useMapStore } from './stores/map'

// list rows: hovering one shows it in the view; a click keeps the view there (and may select it),
// otherwise the view goes back to where it was before the hovering started
export const useHoverFocus = () => {
  const store = useMapStore()
  const history = useHistoryStore()
  let saved: CameraState | null = null
  let back = 0

  const restore = () => {
    if (saved) store.view?.setCamera(saved)
    saved = null
  }

  const enter = (index: number | null) => {
    clearTimeout(back)
    store.view?.showHover(index)
    if (index === null || !store.view) return
    saved ??= store.view.cameraState()
    store.view.focus(index)
  }

  // moving on to the next row doesn't jump back in between
  const leave = () => {
    store.view?.showHover(null)
    clearTimeout(back)
    back = window.setTimeout(restore, 120)
  }

  const keep = (index: number | null, select = true) => {
    clearTimeout(back)
    saved = null
    if (index === null) return
    store.view?.focus(index)
    if (select) history.change(() => (store.selected = index))
  }

  return { enter, leave, keep }
}
