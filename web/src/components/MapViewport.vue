<script setup lang="ts">
import { computed, markRaw, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useMapStore } from '../stores/map'
import { MapScene, type LookChange } from '../viewer/MapScene'
import { iceMaterialName, lookPrefix, wallMaterials } from '../viewer/iceLook'
import ConvertMenu from './ConvertMenu.vue'
import InspectPanel from './InspectPanel.vue'
import { displayName } from '../viewer/names'
import { outcomes } from '../viewer/outcomes'
import { usePreviewStore } from '../stores/preview'
import { useHistoryStore } from '../stores/history'

const store = useMapStore()
const stage = ref<HTMLDivElement>()
const pointer = ref({ x: 0, y: 0 })
// a canvas can't get a WebGL context twice, so each map gets a fresh one
const canvasKey = ref(0)
const canvas = ref<HTMLCanvasElement>()
const preview = usePreviewStore()
const history = useHistoryStore()

// changed pieces drawn as converted where their meshes exist (originals hidden), else in the converter's own materials;
// what's left (swapped, sunk, removed without a mesh) keeps a colour
async function showPreview() {
  const view = store.view
  if (!view) return
  const groups = new Map<number, number[]>()
  const changes: LookChange[] = []
  const result = preview.result
  const hiddenBlocks = new Set(result?.hiddenBlocks)
  const hiddenItems = new Set(result?.hiddenItems)
  if (result)
    preview.decisions?.forEach((decision, index) => {
      const info = store.infos[index]
      const real = typeof info?.block === 'number' ? hiddenBlocks.has(info.block) : typeof info?.item === 'number' && hiddenItems.has(info.item)
      if (real) {
        changes.push({ index, materials: view.materialNames(index).map(() => null), topsOnly: false })
        return
      }
      if (!decision) return
      const prefix = lookPrefix[decision.outcome]
      if (prefix !== undefined) {
        const topsOnly = decision.outcome === 'tops'
        const block = displayName(store.infos[index])
        const materials = view.materialNames(index).map((name) => {
          if (!name || (topsOnly && !wallMaterials.has(name))) return undefined
          const to = iceMaterialName(result.swaps, name, block, prefix)
          if (to === undefined || to === 'native') return topsOnly ? null : undefined
          if (to === '') return null
          return result.looks[to] ? view.lookMaterial(result.looks[to]) : undefined
        })
        changes.push({ index, materials, topsOnly })
        return
      }
      const color = outcomes[decision.outcome]?.color
      if (color == null) return
      const group = groups.get(color)
      if (group) group.push(index)
      else groups.set(color, [index])
    })
  const tints = [...groups].map(([color, indices]) => ({ color, indices }))
  await view.showPreview(result ? { changed: preview.changed, looks: changes, tints } : null)
  preview.lookVersion++
}

const hoverName = computed(() => (store.hovered === null ? '' : displayName(store.infos[store.hovered])))

function createView() {
  return markRaw(
    new MapScene(canvas.value!, {
      hover: (index) => (store.hovered = index),
      select: (index, add) => history.change(() => store.pick(index, add)),
      paint: (index, add) => store.paint(index, add),
      stroke: (phase) => history.stroke(phase),
      isSelected: (index) => store.selection.includes(index),
      textureLoaded: () => store.textureLoads++,
      cameraMoved: () => store.cameraMoves++,
    }),
  )
}

// only this viewport's own view: on a hot reload the new viewport mounts before the old one unmounts
let ownView: MapScene | null = null

function closeView() {
  if (!ownView) return
  ownView.dispose()
  if (store.view === ownView) store.view = null
  ownView = null
}

async function showScene(scene: typeof store.scene) {
  closeView()
  if (!scene) return
  canvasKey.value++
  await nextTick()
  if (store.scene !== scene || !canvas.value) return
  const view = createView()
  view.setHiddenCategories(store.hidden)
  view.load(scene.manifest, scene.geometry)
  view.setSelection(store.selection)
  ownView = view
  store.view = view
  showPreview()
}

watch(() => store.scene, showScene)

watch(() => store.hidden, (hidden) => store.view?.setHiddenCategories(hidden))
watch(() => store.selection, (selection) => store.view?.setSelection(selection))
watch(() => preview.decisions, showPreview)

function onPointerMove(event: PointerEvent) {
  const rect = stage.value!.getBoundingClientRect()
  pointer.value = { x: event.clientX - rect.left, y: event.clientY - rect.top }
}

function onKey(event: KeyboardEvent) {
  if (event.target instanceof HTMLInputElement && event.target.type !== 'checkbox') return
  if (event.key === 'Home') store.view?.frameAll()
  else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') {
    event.preventDefault()
    if (event.shiftKey) history.redo()
    else history.undo()
  } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'y') {
    event.preventDefault()
    history.redo()
  }
}

onMounted(() => {
  window.addEventListener('keydown', onKey)
  // a hot-reloaded viewport mounts with the scene already loaded
  if (store.scene) void showScene(store.scene)
})
onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKey)
  closeView()
})
</script>

<template>
  <main :class="$style.viewport">
    <div ref="stage" :class="$style.stage" @pointermove="onPointerMove">
      <canvas v-show="store.scene" :key="canvasKey" ref="canvas" :class="$style.canvas" />
      <div v-if="store.status" :class="$style.message" role="status">{{ store.status }}</div>
      <div v-else-if="store.loadError" :class="[$style.message, $style.error]" role="alert">
        Couldn't open this map: {{ store.loadError }}
      </div>
      <div v-else-if="!store.scene" :class="$style.message">No map open</div>
      <div
        v-if="hoverName"
        :class="$style.tooltip"
        :style="{ left: `${pointer.x + 14}px`, top: `${pointer.y + 16}px` }"
      >
        {{ hoverName }}
      </div>
      <div v-if="store.scene" :class="$style.right">
        <ConvertMenu :class="$style.section" />
        <InspectPanel :class="$style.section" />
      </div>
    </div>
  </main>
</template>

<style module>
.viewport {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: var(--viewport);
}

.stage {
  position: relative;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.canvas {
  display: block;
  width: 100%;
  height: 100%;
}

.message {
  position: absolute;
  inset: 0;
  display: grid;
  place-items: center;
  padding: 24px;
  color: var(--muted);
  text-align: center;
}

.error {
  color: var(--error);
}

/* properties, then the convert menu, over the view's top right */
.right {
  position: absolute;
  top: 0;
  right: 0;
  z-index: 1;
  display: flex;
  flex-direction: column;
  width: 290px;
  max-height: 100%;
  overflow-y: auto;
  border-bottom: 1px solid var(--line);
  border-left: 1px solid var(--line);
}

.section {
  /* full height each; the column scrolls */
  flex: none;
}

.section + .section {
  border-top: 1px solid var(--line);
}

.tooltip {
  position: absolute;
  padding: 1px 5px;
  color: var(--strong);
  white-space: nowrap;
  pointer-events: none;
  background: var(--field);
  border: 1px solid var(--line);
}
</style>
