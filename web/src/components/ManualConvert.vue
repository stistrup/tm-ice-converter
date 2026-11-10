<script setup lang="ts">
import { computed, watch } from 'vue'
import { useHistoryStore } from '../stores/history'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'

const store = useMapStore()
const preview = usePreviewStore()
const history = useHistoryStore()

const MIXED = '\u0000mixed'

// what the pieces are and can become comes with a preview: work one out as soon as something is selected
watch(
  () => store.selection.length > 0 && !preview.result && !preview.loading,
  (needed) => {
    if (needed) void preview.run()
  },
  { immediate: true },
)

// the selection by kind, each with what its pieces can become and what they're picked to now
const groups = computed(() => {
  const result = preview.result
  if (!result) return []
  const byKind = new Map<string, { keys: string[]; indices: number[] }>()
  for (const index of store.selection) {
    const decision = preview.decisions?.[index]
    if (!decision?.kind) continue
    const group = byKind.get(decision.kind) ?? { keys: [], indices: [] }
    group.keys.push(decision.key)
    group.indices.push(index)
    byKind.set(decision.kind, group)
  }
  return [...byKind].map(([kind, { keys, indices }]) => {
    const values = new Set(keys.map((key) => preview.picks[key] ?? ''))
    return {
      kind,
      label: result.kindLabels[kind] ?? kind,
      keys,
      indices,
      choices: result.choices[kind] ?? [],
      value: values.size === 1 ? [...values][0] : MIXED,
    }
  })
})

function choose(keys: string[], choice: string) {
  history.change(() => preview.pick(keys, choice))
}

function deselect(indices: number[]) {
  const out = new Set(indices)
  history.change(() => (store.selection = store.selection.filter((i) => !out.has(i))))
}
</script>

<template>
  <section :class="$style.menu">
    <div :class="$style.body">
      <p v-if="!preview.result" :class="$style.muted">Working out what these are…</p>
      <div v-for="group in groups" :key="group.kind" :class="$style.group">
        <span :class="$style.name">
          {{ group.label }} <span :class="$style.muted">({{ group.keys.length }})</span>
          <button type="button" :class="$style.deselect" @click="deselect(group.indices)">Deselect</button>
        </span>
        <select
          :class="$style.select"
          :value="group.value"
          :aria-label="group.label"
          @change="choose(group.keys, ($event.target as HTMLSelectElement).value)"
        >
          <option v-if="group.value === MIXED" :value="MIXED" disabled>— mixed —</option>
          <option v-for="choice in group.choices" :key="choice.value" :value="choice.value">{{ choice.label }}</option>
        </select>
      </div>
    </div>
  </section>
</template>

<style module>
.menu {
  display: flex;
  flex-direction: column;
}

.header {
  flex: none;
  height: 24px;
  padding: 0 8px;
  line-height: 24px;
  color: var(--strong);
  background: var(--header);
}

.body {
  display: grid;
  gap: 6px;
}

.group {
  display: grid;
  gap: 2px;
  cursor: default;
}

.name {
  display: flex;
  gap: 6px;
  align-items: center;
}

.deselect {
  height: 16px;
  padding: 0 6px;
  font-size: 11px;
  color: var(--muted);
  cursor: default;
  background: transparent;
  border: 1px solid var(--line);
  border-radius: 2px;
}

.deselect:hover {
  color: var(--text);
  background: #3a3a3a;
}

.select {
  height: 22px;
  padding: 0 4px;
  color: var(--text);
  background: var(--field);
  border: 1px solid var(--line);
  border-radius: 2px;
}

.muted {
  color: var(--muted);
}

.body p {
  margin: 0;
}

.actions {
  display: flex;
  flex: none;
  justify-content: flex-end;
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
</style>
