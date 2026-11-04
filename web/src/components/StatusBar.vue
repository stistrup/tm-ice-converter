<script setup lang="ts">
import { computed } from 'vue'
import { useMapStore } from '../stores/map'

const store = useMapStore()
const manifest = computed(() => store.scene?.manifest)
const noMesh = computed(() => Object.values(manifest.value?.skipped ?? {}).reduce((sum, count) => sum + count, 0))
</script>

<template>
  <footer :class="$style.bar">
    <span v-if="manifest">{{ manifest.map }}</span>
    <span v-if="noMesh" :class="$style.warn" title="Drawn as boxes or cones; see No mesh in the toolbar">
      {{ noMesh }} without mesh
    </span>
    <span
      v-if="manifest?.notExtracted"
      :class="$style.warn"
      title="Run Extract block meshes in game, then Rebuild"
    >
      {{ manifest.notExtracted }} not extracted
    </span>
    <span :class="$style.help">
      Drag move &nbsp; Right-drag pivot &nbsp; Scroll zoom &nbsp; WASD QE move &nbsp; Shift fast
    </span>
  </footer>
</template>

<style module>
.bar {
  display: flex;
  gap: 16px;
  align-items: center;
  padding: 0 8px;
  overflow: hidden;
  color: var(--muted);
  white-space: nowrap;
  background: var(--header);
}

.warn {
  color: var(--warn);
}

.help {
  margin-left: auto;
}
</style>
