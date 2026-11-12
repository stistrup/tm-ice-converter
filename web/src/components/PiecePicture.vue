<script setup lang="ts">
import { markRaw, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'
import { PiecePreview } from '../viewer/PiecePreview'

const store = useMapStore()
const previewStore = usePreviewStore()
const canvas = ref<HTMLCanvasElement>()
const hasMesh = ref(false)
let preview = null as PiecePreview | null

const update = () => {
  const parts = store.selected === null ? null : (store.view?.partsOf(store.selected) ?? null)
  hasMesh.value = parts !== null
  preview?.show(parts)
}

onMounted(() => {
  preview = markRaw(new PiecePreview(canvas.value!))
  update()
})
onBeforeUnmount(() => preview?.dispose())

watch(() => [store.selected, store.view, previewStore.lookVersion], update)
watch(() => store.textureLoads, () => preview?.invalidate())
</script>

<template>
  <div :class="$style.picture">
    <canvas ref="canvas" :class="$style.canvas" />
    <p v-if="!hasMesh" :class="$style.none">No mesh to show</p>
  </div>
</template>

<style module>
.picture {
  position: relative;
  flex: none;
  height: 220px;
  background: var(--field);
}

.canvas {
  display: block;
  width: 100%;
  height: 100%;
}

.none {
  position: absolute;
  inset: 0;
  display: grid;
  place-items: center;
  margin: 0;
  color: var(--muted);
  pointer-events: none;
}
</style>
