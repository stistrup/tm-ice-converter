<script setup lang="ts">
import { usePreviewStore } from '../stores/preview'
import { useHistoryStore } from '../stores/history'
import { optionLabel } from '../viewer/outcomes'
import { grouped, optionGroups, type OptionGroup } from '../viewer/optionGroups'

const preview = usePreviewStore()
const history = useHistoryStore()

function choose(group: OptionGroup, index: number) {
  const choice = group.choices[index]
  if (!choice) return
  history.change(() => {
    for (const [name, value] of Object.entries(choice.set)) preview.set(name, value)
  })
}

// against what the map's own switches add up to
function groupChanged(group: OptionGroup) {
  const own = (name: string) => preview.options.find((o) => o.name === name)?.mapValue ?? false
  return group.current(preview.valueOf) !== group.current(own)
}

</script>

<template>
  <section :class="$style.menu">
    <button
      type="button"
      :class="$style.header"
      :aria-expanded="preview.open"
      @click="preview.open ? preview.close() : preview.show()"
    >
      <span :class="$style.grow">Conversion</span>
      <span v-if="preview.open && preview.result && !preview.loading" :class="$style.muted">{{ (preview.result.milliseconds / 1000).toFixed(1) }} s</span>
      <span :class="[$style.chevron, { [$style.up]: preview.open }]" />
    </button>
    <div v-if="preview.open" :class="$style.body">
      <p v-if="preview.error" :class="$style.error">{{ preview.error }}</p>

      <div v-if="preview.options.length" :class="$style.options">
        <label v-for="group in optionGroups" :key="group.title" :class="$style.choice">
          <span :class="$style.option">
            <span>{{ group.title }}</span>
            <span :class="$style.info" :title="group.description" aria-label="What it does">i</span>
            <span v-if="groupChanged(group)" :class="$style.changed" title="changed from the map's setting">●</span>
          </span>
          <select
            :class="$style.select"
            :value="group.current(preview.valueOf)"
            @change="choose(group, Number(($event.target as HTMLSelectElement).value))"
          >
            <option v-for="(choice, i) in group.choices" :key="choice.label" :value="i">{{ choice.label }}</option>
          </select>
        </label>
        <label v-for="option in preview.options.filter((o) => !grouped.has(o.name))" :key="option.name" :class="$style.option">
          <input
            type="checkbox"
            :checked="preview.valueOf(option.name)"
            @change="history.change(() => preview.set(option.name, ($event.target as HTMLInputElement).checked))"
          />
          <span>{{ optionLabel(option.name) }}</span>
          <span v-if="option.description" :class="$style.info" :title="option.description" aria-label="What it does">i</span>
          <span
            v-if="preview.valueOf(option.name) !== option.mapValue"
            :class="$style.changed"
            title="changed from the map's setting"
          >●</span>
        </label>
      </div>
      <p :class="$style.manualCount">Manual rules: {{ Object.keys(preview.picks).length }}</p>
    </div>
    <div v-if="preview.open && preview.building" :class="$style.building" role="status">
      <span :class="$style.spinner" aria-hidden="true" />
      Building missing pieces {{ preview.building.done }} of {{ preview.building.total }}…
    </div>
    <div v-if="preview.open" :class="$style.actions">
      <button
        type="button"
        :class="$style.button"
        :disabled="preview.loading"
        @click="preview.run()"
      >
        <span v-if="preview.loading" :class="$style.spinner" aria-hidden="true" />
        Calculate Preview
      </button>
      <span :class="$style.grow" />
      <button type="button" :class="[$style.button, $style.primary]" disabled title="not wired up yet">Convert</button>
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

.chevron {
  flex: none;
  width: 8px;
  height: 8px;
  border-right: 1.5px solid var(--muted);
  border-bottom: 1.5px solid var(--muted);
  transform: translateY(-2px) rotate(45deg);
}

.chevron.up {
  transform: translateY(2px) rotate(-135deg);
}

.body {
  min-height: 0;
  padding: 8px;
  overflow-y: auto;
}

.muted {
  color: var(--muted);
}

.error {
  margin: 0 0 8px;
  color: var(--error);
}

.option {
  display: flex;
  gap: 6px;
  align-items: center;
  min-height: 18px;
}

.grow {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.picked {
  margin: 0 0 8px;
  padding: 6px 0;
  border-top: 1px solid var(--line);
  border-bottom: 1px solid var(--line);
}

.options {
  display: grid;
  gap: 2px;
}

.option {
  cursor: default;
}

.option input {
  flex: none;
  width: auto;
  height: auto;
  margin: 0;
}

.choice {
  display: grid;
  gap: 2px;
  margin-bottom: 4px;
  cursor: default;
}

.select {
  height: 22px;
  padding: 0 4px;
  color: var(--text);
  background: var(--field);
  border: 1px solid var(--line);
  border-radius: 2px;
}

.info {
  display: grid;
  flex: none;
  place-items: center;
  width: 13px;
  height: 13px;
  font-size: 9px;
  font-style: italic;
  font-weight: 700;
  color: var(--muted);
  cursor: default;
  border: 1px solid var(--muted);
  border-radius: 50%;
}

.info:hover {
  color: var(--strong);
  border-color: var(--strong);
}

.changed {
  color: var(--accent, #e5912f);
  font-size: 9px;
}

.actions {
  display: flex;
  flex: none;
  gap: 6px;
  align-items: center;
  padding: 0 8px 8px;
}

.button {
  height: 20px;
  padding: 0 10px;
  cursor: default;
  background: #3a3a3a;
  border: 1px solid var(--line);
  border-radius: 2px;
}

.button:hover:not(:disabled) {
  background: #454545;
}

.button:disabled {
  color: var(--muted);
}

.button {
  display: inline-flex;
  gap: 6px;
  align-items: center;
}

.manualCount {
  margin: 8px 0 0;
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
  width: 10px;
  height: 10px;
  border: 1.5px solid currentColor;
  border-right-color: transparent;
  border-radius: 50%;
  animation: spin 0.7s linear infinite;
}

@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}

.primary,
.primary:disabled {
  font-weight: 600;
  color: #1a1a1a;
  background: var(--accent);
}

.primary:hover:not(:disabled) {
  background: #f0a04a;
}
</style>
