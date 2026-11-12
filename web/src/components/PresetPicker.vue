<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { fetchBuildStatus, fetchPresets, type BuildStatus, type Preset } from '../api'
import { useHistoryStore } from '../stores/history'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'

const store = useMapStore()
const preview = usePreviewStore()
const history = useHistoryStore()

const presets = ref<Preset[]>([])
const presetName = ref('')
const applying = ref(false)
// pieces the preset needs get built first
const building = ref<BuildStatus | null>(null)
const status = computed(() =>
  !applying.value ? '' : building.value?.running ? `Building pieces ${building.value.done} of ${building.value.total}…` : 'Applying…',
)

onMounted(async () => {
  presets.value = await fetchPresets().catch(() => [])
  presetName.value = presets.value[0]?.name ?? ''
})

const apply = async (selected: boolean) => {
  if (!presetName.value || applying.value) return
  const only = selected ? store.selection.map((i) => preview.decisions?.[i]?.key).filter((k): k is string => !!k) : null
  if (selected && !only?.length) return
  applying.value = true
  const poll = setInterval(async () => {
    building.value = await fetchBuildStatus().catch(() => null)
  }, 1000)
  try {
    await history.preset(presetName.value, only)
  } finally {
    clearInterval(poll)
    building.value = null
    applying.value = false
  }
}
</script>

<template>
  <div v-if="presets.length" :class="$style.preset">
    <div :class="$style.row">
      <select v-model="presetName" :class="[$style.select, $style.grow]" aria-label="Preset">
        <option v-for="p in presets" :key="p.name" :value="p.name">{{ p.name }}</option>
      </select>
      <span :class="$style.muted">to</span>
      <select :class="$style.select" aria-label="Material" disabled>
        <option>Ice</option>
      </select>
    </div>
    <div :class="$style.row">
      <span :class="[$style.muted, $style.grow]">{{ status }}</span>
      <button
        type="button"
        :class="$style.smallButton"
        :disabled="applying || !store.selection.length || !preview.result"
        :title="store.selection.length && !preview.result ? 'Waits for the preview' : ''"
        @click="apply(true)"
      >
        To selection
      </button>
      <button type="button" :class="$style.smallButton" :disabled="applying" @click="apply(false)">To map</button>
    </div>
  </div>
</template>

<style module>
.preset {
  display: grid;
  gap: 4px;
  margin-bottom: 8px;
}

.row {
  display: flex;
  gap: 6px;
  align-items: center;
}

.select {
  composes: select from '../ui.module.css';
}

.muted {
  composes: muted from '../ui.module.css';
}

.grow {
  composes: grow from '../ui.module.css';
}

.smallButton {
  composes: smallButton from '../ui.module.css';
}
</style>
