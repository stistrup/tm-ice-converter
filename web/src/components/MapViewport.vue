<script setup lang="ts">
import { computed, markRaw, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useMapStore } from '../stores/map'
import { categories, categoryLabels } from '../viewer/categories'
import { MapScene } from '../viewer/MapScene'

const store = useMapStore()
const host = ref<HTMLDivElement>()
const pointer = ref({ x: 0, y: 0 })
// a canvas can't get a WebGL context twice, so each map gets a fresh one
const canvasKey = ref(0)
const canvas = ref<HTMLCanvasElement>()
let view = null as MapScene | null

const hoverInfo = computed(() => (store.hovered === null ? null : store.infos[store.hovered]))
const skippedTotal = computed(() =>
  Object.values(store.scene?.manifest.skipped ?? {}).reduce((sum, count) => sum + count, 0),
)

function createView() {
  return markRaw(
    new MapScene(canvas.value!, {
      hover: (index) => (store.hovered = index),
      select: (index) => (store.selected = index === null ? [] : [index]),
    }),
  )
}

watch(
  () => store.scene,
  async (scene) => {
    view?.dispose()
    view = null
    if (!scene) return
    canvasKey.value++
    await nextTick()
    if (store.scene !== scene) return
    view = createView()
    view.setHiddenCategories(store.hidden)
    view.load(scene.manifest, scene.geometry)
    view.setSelection(store.selected)
  },
)

watch(() => store.hidden, (hidden) => view?.setHiddenCategories(hidden))
watch(() => store.selected, (selected) => view?.setSelection(selected))
watch(() => store.focusRequest, () => view?.focus(store.selected))

function onPointerMove(event: PointerEvent) {
  const rect = host.value!.getBoundingClientRect()
  pointer.value = { x: event.clientX - rect.left, y: event.clientY - rect.top }
}

onBeforeUnmount(() => view?.dispose())
</script>

<template>
  <main ref="host" :class="$style.viewport" @pointermove="onPointerMove">
    <canvas v-show="store.scene" :key="canvasKey" ref="canvas" :class="$style.canvas" />

    <div v-if="store.status" :class="$style.centre" role="status">{{ store.status }}</div>
    <div v-else-if="store.loadError" :class="[$style.centre, $style.error]" role="alert">
      Couldn't open this map: {{ store.loadError }}
    </div>
    <div v-else-if="!store.scene" :class="$style.centre">Pick a map on the left.</div>

    <template v-if="store.scene">
      <div :class="$style.toolbar">
        <label v-for="category in categories" :key="category" :class="$style.toggle">
          <input
            type="checkbox"
            :checked="!store.hidden.includes(category)"
            @change="store.toggleCategory(category)"
          />
          {{ categoryLabels[category] }}
        </label>
        <button type="button" :class="$style.button" @click="view?.frameAll()">Show all</button>
        <button
          type="button"
          :class="$style.button"
          title="Read the map again, for newly extracted game files"
          @click="store.currentMap && store.openMap(store.currentMap, true)"
        >
          Rebuild
        </button>
      </div>

      <p :class="$style.hint">
        Left drag: orbit · Right drag: pan · Scroll: zoom · WASD, Q/E: move (Shift: fast) · Click: select
      </p>
      <p v-if="skippedTotal || store.scene.manifest.notExtracted" :class="$style.notice">
        <span v-if="skippedTotal">{{ skippedTotal }} pieces without a mesh</span>
        <span v-if="skippedTotal && store.scene.manifest.notExtracted"> · </span>
        <span v-if="store.scene.manifest.notExtracted">
          {{ store.scene.manifest.notExtracted }} files not extracted yet: run Extract block meshes in game, then Rebuild
        </span>
      </p>

      <div
        v-if="hoverInfo"
        :class="$style.tooltip"
        :style="{ left: `${pointer.x + 14}px`, top: `${pointer.y + 14}px` }"
      >
        {{ hoverInfo.name }}
      </div>
    </template>
  </main>
</template>

<style module>
.viewport {
  position: relative;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}

.canvas {
  display: block;
  width: 100%;
  height: 100%;
}

.centre {
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

.toolbar {
  position: absolute;
  top: 10px;
  left: 10px;
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  align-items: center;
  max-width: calc(100% - 20px);
  padding: 6px 10px;
  background: color-mix(in srgb, var(--panel) 88%, transparent);
  border: 1px solid var(--line);
  border-radius: 6px;
}

.toggle {
  display: flex;
  gap: 5px;
  align-items: center;
  cursor: pointer;
}

.toggle input {
  width: auto;
  accent-color: var(--accent);
}

.button {
  padding: 2px 8px;
  cursor: pointer;
  background: var(--panel-raised);
  border: 1px solid var(--line);
  border-radius: 4px;
}

.button:hover {
  border-color: var(--accent);
}

.hint,
.notice {
  position: absolute;
  left: 10px;
  max-width: calc(100% - 20px);
  margin: 0;
  padding: 3px 8px;
  font-size: 12px;
  background: color-mix(in srgb, var(--panel) 80%, transparent);
  border-radius: 4px;
}

.hint {
  bottom: 10px;
  color: var(--muted);
}

.notice {
  bottom: 40px;
  color: var(--warn);
}

.tooltip {
  position: absolute;
  max-width: 420px;
  padding: 3px 8px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  pointer-events: none;
  background: var(--panel-raised);
  border: 1px solid var(--line);
  border-radius: 4px;
}
</style>
