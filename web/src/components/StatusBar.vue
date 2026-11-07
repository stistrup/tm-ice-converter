<script setup lang="ts">
import { computed } from 'vue'
import { useMapStore } from '../stores/map'
import { useSetupStore } from '../stores/setup'

const store = useMapStore()
const setup = useSetupStore()
const manifest = computed(() => store.scene?.manifest)
const noMesh = computed(() => Object.values(manifest.value?.skipped ?? {}).reduce((sum, count) => sum + count, 0))
const noMeshList = computed(() =>
  Object.entries(manifest.value?.skipped ?? {})
    .sort((a, b) => b[1] - a[1])
    .map(([what, count]) => `${count}× ${what}`)
    .join('\n'),
)
</script>

<template>
  <footer :class="$style.bar">
    <span v-if="manifest">{{ store.currentMap ? store.currentMap.path.replace(/\.Map\.Gbx$/i, '') : manifest.map }}</span>
    <span v-if="noMesh" :class="$style.warn" :title="`Drawn as boxes or cones:\n${noMeshList}`">
      {{ noMesh }} without mesh
    </span>
    <span
      v-if="manifest?.notExtracted.length"
      :class="$style.warn"
      :title="`Run Extract game files in game, then Rebuild:\n${manifest.notExtracted.join('\n')}`"
    >
      {{ manifest.notExtracted.length }} not extracted
    </span>
    <span :class="$style.help">
      Drag move &nbsp; Right-drag pivot &nbsp; Scroll zoom &nbsp; WASD QE move &nbsp; Shift fast
    </span>
    <button :class="[$style.setup, { [$style.warn]: setup.status && !setup.status.ready }]" type="button" @click="setup.open()">Setup</button>
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

.setup {
  padding: 0 4px;
  color: inherit;
  cursor: default;
  background: none;
  border: 0;
}

.setup:hover {
  color: var(--strong);
}
</style>
