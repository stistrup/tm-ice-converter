<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { fetchVersion, type VersionInfo } from '../api'
import { useMapStore } from '../stores/map'
import { useSetupStore } from '../stores/setup'
import { displayName } from '../viewer/names'
import FloatingPanel from './FloatingPanel.vue'
import { useHoverFocus } from '../hoverFocus'
import FocusEye from './FocusEye.vue'

const store = useMapStore()
const setup = useSetupStore()
const manifest = computed(() => store.scene?.manifest)
const version = ref<VersionInfo | null>(null)
onMounted(async () => {
  version.value = await fetchVersion().catch(() => null)
})
// boxes and cones, numbered after the instances as MapScene does
const missing = computed(() => {
  const m = manifest.value
  if (!m) return []
  const start = m.instances.length
  return [...m.placeholders, ...m.boxes]
    .map((p, i) => ({ index: start + i, name: displayName(p.info), cell: Array.isArray(p.info.cell) ? p.info.cell.join(', ') : '', kind: i < m.placeholders.length ? 'cone' : 'box' }))
    .sort((a, b) => a.name.localeCompare(b.name))
})
const missingOpen = ref(false)
const missingButton = ref<HTMLElement>()
const { enter, leave, keep } = useHoverFocus()
watch(missingOpen, (now) => now || leave())
watch(manifest, () => (missingOpen.value = false))
</script>

<template>
  <footer :class="$style.bar">
    <span v-if="manifest">{{ store.currentMap ? store.currentMap.path.replace(/\.Map\.Gbx$/i, '') : manifest.map }}</span>
    <button v-if="missing.length" ref="missingButton" type="button" :class="$style.missing" @click="missingOpen = !missingOpen">
      {{ missing.length }} without mesh
    </button>
    <FloatingPanel v-model="missingOpen" :anchor="missingButton" :title="`Without mesh, drawn as boxes or cones: ${missing.length}`">
      <li v-for="piece in missing" :key="piece.index" @click="keep(piece.index)">
        <span :class="$style.grow">{{ piece.name }}</span>
        <span>{{ piece.cell }}</span>
        <span>{{ piece.kind }}</span>
        <FocusEye @enter="enter(piece.index)" @leave="leave()" @keep="keep(piece.index)" />
      </li>
    </FloatingPanel>
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
    <a v-if="version?.newer && version.url" :class="[$style.link, $style.warn]" :href="version.url" target="_blank" rel="noopener">
      {{ version.latest }} available
    </a>
    <span v-if="version">v{{ version.current }}</span>
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

.grow {
  composes: grow from '../ui.module.css';
  color: var(--text);
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

.missing {
  padding: 0 4px;
  color: var(--warn);
  cursor: default;
  background: none;
  border: 0;
}

.missing:hover {
  text-decoration: underline;
}

.link {
  text-decoration: none;
}

.link:hover {
  text-decoration: underline;
}
</style>
