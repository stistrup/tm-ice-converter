import { watch } from 'vue'
import { useMapStore } from './stores/map'
import { usePreviewStore } from './stores/preview'
import type { CameraState } from './viewer/MapScene'
import { displayName } from './viewer/names'

// the page reports what's on screen and takes commands, so a terminal can follow along (see GuiServer /api/state)
type Command =
  | { type: 'shot'; id: string }
  | { type: 'select'; index?: number; name?: string; focus?: boolean }
  | { type: 'camera' } & CameraState
  | { type: 'open'; map?: string; rebuild?: boolean }
  | { type: 'pick'; id: string; x: number; y: number }
  | { type: 'preview'; open: boolean; set?: Record<string, boolean> }

// once per page: a hot-reloaded App mounts again, and every extra event stream takes one of the browser's few connections
let started = false

export function startLiveLink() {
  if (started) return
  started = true
  const store = useMapStore()

  function piece(index: number | null) {
    if (index === null || !store.infos[index]) return null
    const position = store.view?.positionOf(index)
    const size = store.view?.sizeOf(index)
    const round = (n: number) => Math.round(n * 10) / 10
    return {
      index,
      name: displayName(store.infos[index]),
      position: position && [position.x, position.y, position.z].map(round),
      size: size && [size.x, size.y, size.z].map(round),
      info: store.infos[index],
    }
  }

  let timer = 0
  function report() {
    clearTimeout(timer)
    timer = window.setTimeout(() => {
      const state = {
        updated: new Date().toISOString(),
        map: store.currentMap && { path: store.currentMap.path, name: store.currentMap.name },
        loaded: !!store.scene,
        selected: piece(store.selected),
        selection: store.selection,
        hovered: piece(store.hovered),
        camera: store.view?.cameraState() ?? null,
        hidden: store.hidden,
      }
      void fetch('/api/state', { method: 'POST', body: JSON.stringify(state) }).catch(() => {})
    }, 250)
  }

  watch(() => [store.currentMap, store.scene, store.selected, store.hovered, store.hidden, store.cameraMoves, store.view], report)

  async function run(command: Command) {
    if (command.type === 'shot') {
      if (!store.view) return
      const png = await (await fetch(store.view.capture())).blob()
      await fetch(`/api/answer?id=${command.id}`, { method: 'POST', body: png })
    } else if (command.type === 'select') {
      const index = command.index ?? (command.name ? store.infos.findIndex((info) => info.name === command.name) : -1)
      if (index < 0 || index >= store.infos.length) return
      store.selected = index
      if (command.focus) store.view?.focus(index)
    } else if (command.type === 'camera') {
      store.view?.setCamera(command)
    } else if (command.type === 'pick') {
      const found = store.view?.pickDetail(command.x, command.y) ?? null
      const answer = found && { ...found, piece: piece(found.index) }
      await fetch(`/api/answer?id=${command.id}`, { method: 'POST', body: JSON.stringify(answer) })
    } else if (command.type === 'preview') {
      const preview = usePreviewStore()
      if (!command.open) preview.close()
      else {
        for (const [name, value] of Object.entries(command.set ?? {})) preview.set(name, value)
        preview.show()
        void preview.run()
      }
    } else if (command.type === 'open') {
      const map = command.map
        ? store.maps.find((m) => m.path === command.map || m.name === command.map)
        : store.currentMap
      if (map) void store.openMap(map, command.rebuild)
    }
  }

  // a server restart can leave the stream hanging open behind the Vite proxy, or closed for good: reconnect on silence
  let events: EventSource | null = null
  let heard = Date.now()
  function connect() {
    events?.close()
    heard = Date.now()
    events = new EventSource('/api/events')
    events.onmessage = (event) => {
      heard = Date.now()
      const command = JSON.parse(event.data) as Command | { type: 'ping' }
      if (command.type !== 'ping') void run(command)
    }
    events.onopen = report
  }
  setInterval(() => {
    if (Date.now() - heard > 5000) connect()
  }, 1000)
  connect()
}
