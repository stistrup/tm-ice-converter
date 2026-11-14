<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { inGroup, useAffected, useApplying } from '../affected'
import { fetchPresets, type Preset } from '../api'
import { useHistoryStore } from '../stores/history'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'
import DropDown from './DropDown.vue'

const store = useMapStore()
const preview = usePreviewStore()
const history = useHistoryStore()

const presets = ref<Preset[]>([])
const presetName = ref('')
onMounted(async () => {
  presets.value = await fetchPresets().catch(() => [])
  presetName.value = presets.value[0]?.name ?? ''
})

const preset = computed(() => presets.value.find((p) => p.name === presetName.value))
const { affected, selectedByUs, toggle, keys } = useAffected(() => (d) => preset.value?.steps.some((step) => inGroup(d.group, step.group)) ?? false)
const { applying, status, run } = useApplying()

const apply = () => {
  const only = keys()
  if (presetName.value && only.length) void run(() => history.preset(presetName.value, only))
}
</script>

<template>
  <div v-if="presets.length" :class="$style.preset">
    <div :class="$style.row">
      <span :class="$style.title">Preset</span>
      <DropDown v-model="presetName" :options="presets.map((p) => ({ value: p.name, label: p.name }))" aria-label="Preset" />
      <span :class="$style.muted">to</span>
      <DropDown model-value="ice" :options="[{ value: 'ice', label: 'Ice' }]" narrow disabled aria-label="Material" />
    </div>
    <div :class="$style.actions">
      <span :class="[$style.muted, $style.grow]">{{ status }}</span>
      <button
        type="button"
        :class="$style.smallButton"
        :disabled="applying || (!affected.length && !selectedByUs)"
        :title="preview.result ? '' : 'Waits for the preview'"
        @click="toggle"
      >
        {{ selectedByUs ? 'Deselect' : 'Select affected blocks' }}
      </button>
      <button
        type="button"
        :class="$style.smallButton"
        :disabled="applying || !store.selection.length || !preview.result"
        :title="!store.selection.length ? 'Select blocks first' : ''"
        @click="apply"
      >
        Apply
      </button>
    </div>
  </div>
</template>

<style module>
.preset {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 4px;
  margin-bottom: 8px;
}

.row {
  display: flex;
  gap: 6px;
  align-items: center;
}

.actions {
  composes: row;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.title {
  flex: none;
  width: 44px;
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
