<script setup lang="ts">
import { computed, markRaw, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useMapStore } from '../stores/map'
import InspectPanel from './InspectPanel.vue'
import { categories, categoryLabels } from '../viewer/categories'
import { MapScene } from '../viewer/MapScene'
import { displayName } from '../viewer/names'

const store = useMapStore()
const stage = ref<HTMLDivElement>()
const pointer = ref({ x: 0, y: 0 })
// a canvas can't get a WebGL context twice, so each map gets a fresh one
const canvasKey = ref(0)
const canvas = ref<HTMLCanvasElement>()

const hoverName = computed(() => (store.hovered === null ? '' : displayName(store.infos[store.hovered])))
const toggles = computed(() =>
  categories
    .map((category) => ({
      category,
      label: categoryLabels[category],
      count: store.categoryCounts.get(category) ?? 0,
      on: !store.hidden.includes(category),
    }))
    .filter((toggle) => toggle.count > 0),
)

function createView() {
  return markRaw(
    new MapScene(canvas.value!, {
      hover: (index) => (store.hovered = index),
      // clicking the selected piece again clears the selection
      select: (index) => (store.selected = index === store.selected ? null : index),
      textureLoaded: () => store.textureLoads++,
      cameraMoved: () => store.cameraMoves++,
    }),
  )
}

function closeView() {
  store.view?.dispose()
  store.view = null
}

watch(
  () => store.scene,
  async (scene) => {
    closeView()
    if (!scene) return
    canvasKey.value++
    await nextTick()
    if (store.scene !== scene) return
    const view = createView()
    view.setHiddenCategories(store.hidden)
    view.load(scene.manifest, scene.geometry)
    view.setSelection(store.selected)
    store.view = view
  },
)

watch(() => store.hidden, (hidden) => store.view?.setHiddenCategories(hidden))
watch(() => store.selected, (selected) => store.view?.setSelection(selected))

function onPointerMove(event: PointerEvent) {
  const rect = stage.value!.getBoundingClientRect()
  pointer.value = { x: event.clientX - rect.left, y: event.clientY - rect.top }
}

onBeforeUnmount(closeView)
</script>

<template>
  <main :class="$style.viewport">
    <div :class="$style.toolbar">
      <template v-if="store.scene">
        <button
          v-for="toggle in toggles"
          :key="toggle.category"
          type="button"
          :class="[$style.toggle, { [$style.on]: toggle.on }]"
          :aria-pressed="toggle.on"
          @click="store.toggleCategory(toggle.category)"
        >
          {{ toggle.label }} <span :class="$style.count">{{ toggle.count }}</span>
        </button>
        <span :class="$style.gap" />
        <button type="button" :class="[$style.toggle, $style.action]" @click="store.view?.frameAll()">Frame all</button>
        <button
          type="button"
          :class="[$style.toggle, $style.action]"
          title="Read the map again, for newly extracted game files"
          @click="store.currentMap && store.openMap(store.currentMap, true)"
        >
          Rebuild
        </button>
      </template>
    </div>

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
      <InspectPanel />
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

.toolbar {
  display: flex;
  flex: none;
  gap: 1px;
  align-items: stretch;
  height: 24px;
  overflow: hidden;
  background: var(--header);
}

.toggle {
  flex: none;
  padding: 0 8px;
  color: var(--muted);
  cursor: default;
  background: none;
  border: 0;
}

.toggle:hover {
  color: var(--strong);
  background: var(--hover);
}

.on {
  color: var(--strong);
  background: #3a3a3a;
}

.action {
  color: var(--text);
}

.count {
  color: var(--muted);
}

.gap {
  flex: 1;
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
