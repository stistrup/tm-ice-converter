<script setup lang="ts">
import { useHistoryStore } from '../stores/history'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'
import GroupConversion from './GroupConversion.vue'
import OptionList from './OptionList.vue'
import TargetList from './TargetList.vue'
import PresetPicker from './PresetPicker.vue'

const preview = usePreviewStore()
const store = useMapStore()
const history = useHistoryStore()

</script>

<template>
  <section :class="$style.menu">
    <button
      type="button"
      :class="$style.header"
      :aria-expanded="preview.open"
      @click="preview.open ? preview.close() : preview.show()"
    >
      <span :class="$style.grow">Conversion options</span>
      <span v-if="preview.open && preview.result && !preview.loading" :class="$style.muted">{{ (preview.result.milliseconds / 1000).toFixed(1) }} s</span>
      <span :class="[$style.chevron, { [$style.up]: preview.open }]" />
    </button>
    <div v-if="preview.open" :class="$style.body">
      <p v-if="preview.error" :class="$style.error">{{ preview.error }}</p>

      <div v-if="preview.dirty" :class="$style.unsaved">
        <span :class="$style.grow">Not saved</span>
        <button type="button" :class="$style.textButton" @click="history.revert()">Revert</button>
        <button type="button" :class="$style.smallButton" @click="preview.save()">Save</button>
      </div>

      <PresetPicker v-if="preview.state" />
      <GroupConversion v-if="preview.state" />


      <div v-if="preview.result?.problems.length || preview.result?.stalePicks.length || preview.state?.unreadable.length" :class="$style.problems">
        <p v-for="problem in preview.result?.problems ?? []" :key="problem">Left out: {{ problem }}</p>
        <p v-if="preview.result?.stalePicks.length" :title="preview.result.stalePicks.join('\n')">
          {{ preview.result.stalePicks.length }} conversion target(s) for pieces not on this map
        </p>
        <p v-for="file in preview.state?.unreadable ?? []" :key="file">Unreadable: {{ file }}</p>
      </div>

      <OptionList />
      <TargetList />
    </div>
    <div v-if="preview.open && preview.building" :class="$style.building" role="status">
      <span :class="$style.spinner" aria-hidden="true" />
      Building missing pieces {{ preview.building.done }} of {{ preview.building.total }}…
    </div>
    <div v-if="preview.open" :class="$style.actions">
      <label :class="$style.option">
        <input type="radio" name="shown" :checked="preview.showOriginal" @change="preview.showOriginal = true" />
        <span>Original</span>
      </label>
      <label :class="$style.option">
        <input type="radio" name="shown" :checked="!preview.showOriginal" @change="preview.showOriginal = false" />
        <span>Preview</span>
        <span v-if="!preview.showOriginal && preview.loading" :class="$style.spinner" aria-label="Calculating" />
      </label>
    </div>
    <div v-if="preview.open && store.currentMap" :class="$style.actions">
      <label :class="$style.option">
        <input type="checkbox" :checked="store.chosenMaps.has(store.currentMap.path)" @change="store.choose([store.currentMap.path], ($event.target as HTMLInputElement).checked)" />
        <span>Add to conversion list</span>
      </label>
    </div>
  </section>
</template>

<style module>
.menu {
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--panel);
}

.header {
  display: flex;
  flex: none;
  gap: 8px;
  align-items: center;
  width: 100%;
  height: 24px;
  padding: 0 8px;
  color: var(--strong);
  text-align: left;
  cursor: default;
  background: var(--header);
  border: 0;
}

.body {
  min-height: 0;
  padding: 8px;
  overflow-y: auto;
}

.error {
  composes: error from '../ui.module.css';
  margin-bottom: 8px;
}

.actions {
  display: flex;
  flex: none;
  gap: 6px;
  align-items: center;
  padding: 0 8px 8px;
}

.problems {
  display: grid;
  gap: 2px;
  margin-bottom: 8px;
  padding: 4px 6px;
  color: var(--accent);
  border: 1px solid var(--line);
  border-radius: 2px;
}

.problems p {
  margin: 0;
  overflow-wrap: anywhere;
}

.unsaved {
  display: flex;
  gap: 6px;
  align-items: center;
  margin-bottom: 8px;
  color: var(--muted);
}

.building {
  display: flex;
  gap: 6px;
  align-items: center;
  padding: 0 8px 8px;
  color: var(--muted);
}

.spinner {
  flex: none;
  width: 10px;
  height: 10px;
  border: 1.5px solid var(--accent);
  border-right-color: transparent;
  border-radius: 50%;
  animation: spin 0.7s linear infinite;
}

@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}

.muted {
  composes: muted from '../ui.module.css';
}

.option {
  composes: option from '../ui.module.css';
}

.info {
  composes: info from '../ui.module.css';
}

.grow {
  composes: grow from '../ui.module.css';
}

.chevron {
  composes: chevron from '../ui.module.css';
}

.up {
  composes: up from '../ui.module.css';
}

.smallButton {
  composes: smallButton from '../ui.module.css';
}

.textButton {
  composes: textButton from '../ui.module.css';
}
</style>
