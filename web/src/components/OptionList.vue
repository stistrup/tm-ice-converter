<script setup lang="ts">
import { computed } from 'vue'
import { useHistoryStore } from '../stores/history'
import { usePreviewStore } from '../stores/preview'
import DropDown from './DropDown.vue'
import { coverMaterials, coverOptions, grouped, optionGroups, terrainOptions, type OptionGroup } from '../viewer/optionGroups'
import { optionLabel } from '../viewer/outcomes'

const preview = usePreviewStore()
const history = useHistoryStore()

const terrain = computed(() => preview.options.filter((o) => terrainOptions.includes(o.name)))
const checkboxes = computed(() => [
  ...preview.options.filter((o) => !grouped.has(o.name) && o.category !== 'piece'),
  ...terrain.value,
])

const choose = (group: OptionGroup, index: number) => {
  const choice = group.choices[index]
  if (!choice) return
  history.change(() => {
    for (const [name, value] of Object.entries(choice.set)) preview.set(name, value)
  })
}

const toggle = (name: string, event: Event) => history.change(() => preview.set(name, (event.target as HTMLInputElement).checked))
</script>

<template>
  <fieldset v-if="preview.options.length" :class="$style.options" >
    <div v-for="group in optionGroups" :key="group.title" :class="$style.choice">
      <span :class="$style.option">
        <span>{{ group.title }}</span>
        <span :class="$style.info" :title="group.description" aria-label="What it does">i</span>
      </span>
      <DropDown
        :model-value="group.current(preview.valueOf)"
        :options="group.choices.map((choice, i) => ({ value: i, label: choice.label }))"
        :aria-label="group.title"
        @update:model-value="(i) => choose(group, i)"
      />
    </div>
    <template v-for="option in checkboxes" :key="option.name">
      <label :class="$style.option">
        <input type="checkbox" :checked="preview.valueOf(option.name)" @change="toggle(option.name, $event)" />
        <span>{{ optionLabel(option.name) }}</span>
        <span v-if="option.description" :class="$style.info" :title="option.description" aria-label="What it does">i</span>
      </label>
      <div v-if="coverOptions.has(option.name) && preview.valueOf(option.name)" :class="$style.material">
        <span :class="$style.muted">to</span>
        <DropDown
          :model-value="coverMaterials[0]"
          :options="coverMaterials.map((m) => ({ value: m, label: m }))"
          narrow
          disabled
          aria-label="Material"
        />
      </div>
    </template>
  </fieldset>
</template>

<style module>
.options {
  display: grid;
  gap: 2px;
  min-width: 0;
  margin: 0;
  padding: 0;
  border: 0;
}

.options:disabled {
  opacity: 0.5;
}

.choice {
  display: grid;
  gap: 2px;
  margin-bottom: 4px;
  cursor: default;
}

.option {
  composes: option from '../ui.module.css';
}

.info {
  composes: info from '../ui.module.css';
}

.select {
  composes: select from '../ui.module.css';
}

.material {
  display: flex;
  gap: 6px;
  align-items: center;
  margin: 0 0 2px 22px;
}

.muted {
  composes: muted from '../ui.module.css';
}
</style>
