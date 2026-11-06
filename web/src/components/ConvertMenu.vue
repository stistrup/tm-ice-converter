<script setup lang="ts">
import { computed, ref } from 'vue'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'
import { useHistoryStore } from '../stores/history'
import { optionLabel } from '../viewer/outcomes'
import { grouped, optionGroups, type OptionGroup } from '../viewer/optionGroups'

const preview = usePreviewStore()
const store = useMapStore()
const history = useHistoryStore()

function choose(group: OptionGroup, index: number) {
  const choice = group.choices[index]
  if (!choice) return
  history.change(() => {
    for (const [name, value] of Object.entries(choice.set)) preview.set(name, value)
  })
}

// against what the map's maps.jsonc entry adds up to
function groupChanged(group: OptionGroup) {
  return group.current(preview.valueOf) !== group.current(preview.seedValueOf)
}

const SEED = 'maps.jsonc'

const rulesOpen = ref(false)
const normal = (key: string) => key.trim().replace(/\s*,\s*/g, ',')

// each manual rule with the piece it's on (null: not on this map) and what it picks
const rules = computed(() => {
  const at = new Map<string, number>()
  preview.decisions?.forEach((d, i) => {
    if (d?.key) at.set(normal(d.key), i)
  })
  const result = preview.result
  return Object.entries(preview.picks).map(([key, choice]) => {
    const index = at.get(normal(key)) ?? null
    const kind = index === null ? undefined : preview.decisions?.[index]?.kind
    const label = (kind && result?.choices[kind]?.find((c) => c.value === choice)?.label) || choice
    const name = /^\S+/.exec(key)?.[0] ?? key
    return { key, name, label, index }
  })
})

function hoverRule(index: number | null) {
  store.view?.showHover(index)
  if (index !== null) store.view?.focus(index)
}

function removeRule(key: string) {
  hoverRule(null)
  history.change(() => preview.pick([key], ''))
}

function when(time: string) {
  return new Date(time).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}

// where the working settings stand
const source = computed(() => {
  const state = preview.state
  if (!state) return ''
  const run = state.runs.find((r) => r.id === preview.from)
  const start = run ? `run ${when(run.time)}` : preview.from === SEED || !preview.from ? SEED : 'a run no longer logged'
  return state.from === SEED ? SEED : `Draft, from ${start}`
})

function loadFrom(value: string) {
  const runs = preview.state?.runs ?? []
  history.change(() => preview.load(value === SEED ? null : (runs.find((r) => r.id === value) ?? null)))
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

      <div v-if="preview.state" :class="$style.source">
        <label :class="$style.choice">
          <span :class="$style.option">
            <span :class="$style.grow" :title="preview.state.familyName ? `Converted from ${preview.state.familyName}` : ''">Settings: {{ source }}</span>
          </span>
          <select :class="$style.select" value="" @change="loadFrom(($event.target as HTMLSelectElement).value); ($event.target as HTMLSelectElement).value = ''">
            <option value="" disabled>Load settings from…</option>
            <option :value="SEED">maps.jsonc (start over)</option>
            <option v-for="run in preview.state.runs" :key="run.id" :value="run.id">
              {{ when(run.time) }} · {{ run.kind }} · {{ run.outputName }}{{ run.leftOut ? ` (${run.leftOut} left out)` : '' }}
            </option>
          </select>
        </label>
        <p v-if="!preview.state.runs.length" :class="$style.muted">No conversions of this map yet.</p>
      </div>

      <div v-if="preview.result?.problems.length || preview.result?.stalePicks.length || preview.state?.unreadable.length" :class="$style.problems">
        <p v-for="problem in preview.result?.problems ?? []" :key="problem">Left out: {{ problem }}</p>
        <p v-if="preview.result?.stalePicks.length" :title="preview.result.stalePicks.join('\n')">
          {{ preview.result.stalePicks.length }} manual rule(s) for pieces not on this map
        </p>
        <p v-for="file in preview.state?.unreadable ?? []" :key="file">Unreadable: {{ file }}</p>
      </div>

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
            v-if="preview.valueOf(option.name) !== preview.seedValueOf(option.name)"
            :class="$style.changed"
            title="changed from the map's setting"
          >●</span>
        </label>
      </div>
      <div :class="$style.rules">
        <button
          type="button"
          :class="$style.rulesHeader"
          :aria-expanded="rulesOpen"
          :disabled="!rules.length"
          @click="rulesOpen = !rulesOpen"
        >
          <span>Manual rules: {{ rules.length }}</span>
          <span v-if="rules.length" :class="[$style.chevron, { [$style.up]: rulesOpen }]" />
        </button>
        <ul v-if="rulesOpen && rules.length" :class="$style.ruleList">
          <li
            v-for="rule in rules"
            :key="rule.key"
            :class="[$style.rule, { [$style.gone]: rule.index === null }]"
            :title="rule.index === null ? `${rule.key}: not on this map` : rule.key"
            @mouseenter="hoverRule(rule.index)"
            @mouseleave="hoverRule(null)"
          >
            <span :class="$style.grow">{{ rule.name }}</span>
            <span :class="$style.ruleChoice">{{ rule.label }}</span>
            <button type="button" :class="$style.revert" @click="removeRule(rule.key)">Revert rule</button>
          </li>
        </ul>
      </div>
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

.source {
  margin-bottom: 8px;
  padding-bottom: 8px;
  border-bottom: 1px solid var(--line);
}

.source p {
  margin: 0;
}

.problems {
  display: grid;
  gap: 2px;
  margin-bottom: 8px;
  padding: 4px 6px;
  color: var(--accent, #e5912f);
  border: 1px solid var(--line);
  border-radius: 2px;
}

.problems p {
  margin: 0;
  overflow-wrap: anywhere;
}

.rules {
  margin-top: 8px;
}

.rulesHeader {
  display: flex;
  gap: 6px;
  align-items: center;
  width: 100%;
  height: 20px;
  padding: 0;
  color: var(--muted);
  text-align: left;
  cursor: default;
  background: none;
  border: 0;
}

.rulesHeader:hover:not(:disabled) {
  color: var(--strong);
}

.ruleList {
  display: grid;
  max-height: 220px;
  margin: 2px 0 0;
  padding: 0;
  overflow-y: auto;
  list-style: none;
  border: 1px solid var(--line);
  border-radius: 2px;
}

.rule {
  display: flex;
  gap: 6px;
  align-items: center;
  min-height: 20px;
  padding: 0 2px 0 6px;
}

.rule:hover {
  background: var(--field);
}

.gone {
  opacity: 0.5;
}

.ruleChoice {
  flex: none;
  color: var(--muted);
}

.revert {
  flex: none;
  height: 18px;
  padding: 0 6px;
  color: var(--muted);
  cursor: default;
  background: none;
  border: 0;
  border-radius: 2px;
}

.revert:hover {
  color: var(--error);
  background: var(--line);
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
