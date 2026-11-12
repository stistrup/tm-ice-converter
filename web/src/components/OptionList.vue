<script setup lang="ts">
import { computed, ref } from 'vue'
import { useHistoryStore } from '../stores/history'
import { usePreviewStore } from '../stores/preview'
import { grouped, optionGroups, terrainOptions, type OptionGroup } from '../viewer/optionGroups'
import { optionLabel } from '../viewer/outcomes'

const preview = usePreviewStore()
const history = useHistoryStore()

const terrainOpen = ref(false)
const terrain = computed(() => preview.options.filter((o) => terrainOptions.includes(o.name)))

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
  <fieldset v-if="preview.options.length" :class="$style.options" :disabled="!preview.rulesOn">
    <label v-for="group in optionGroups" :key="group.title" :class="$style.choice">
      <span :class="$style.option">
        <span>{{ group.title }}</span>
        <span :class="$style.info" :title="group.description" aria-label="What it does">i</span>
      </span>
      <select :class="$style.select" :value="group.current(preview.valueOf)" @change="choose(group, Number(($event.target as HTMLSelectElement).value))">
        <option v-for="(choice, i) in group.choices" :key="choice.label" :value="i">{{ choice.label }}</option>
      </select>
    </label>
    <label v-for="option in preview.options.filter((o) => !grouped.has(o.name))" :key="option.name" :class="$style.option">
      <input type="checkbox" :checked="preview.valueOf(option.name)" @change="toggle(option.name, $event)" />
      <span>{{ optionLabel(option.name) }}</span>
      <span v-if="option.description" :class="$style.info" :title="option.description" aria-label="What it does">i</span>
    </label>
    <button v-if="terrain.length" type="button" :class="$style.fold" :aria-expanded="terrainOpen" @click="terrainOpen = !terrainOpen">
      <span>Terrain</span>
      <span :class="[$style.chevron, { [$style.up]: terrainOpen }]" />
    </button>
    <div v-if="terrainOpen" :class="$style.nested">
      <label v-for="option in terrain" :key="option.name" :class="$style.option">
        <input type="checkbox" :checked="preview.valueOf(option.name)" @change="toggle(option.name, $event)" />
        <span>{{ optionLabel(option.name) }}</span>
        <span v-if="option.description" :class="$style.info" :title="option.description" aria-label="What it does">i</span>
      </label>
    </div>
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

.nested {
  display: grid;
  gap: 2px;
  padding-left: 14px;
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

.fold {
  composes: fold from '../ui.module.css';
}

.chevron {
  composes: chevron from '../ui.module.css';
}

.up {
  composes: up from '../ui.module.css';
}
</style>
