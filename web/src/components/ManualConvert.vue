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
  const byKind = new Map<string, string[]>()
  for (const index of store.selection) {
    const decision = preview.decisions?.[index]
    if (!decision?.kind) continue
    byKind.set(decision.kind, [...(byKind.get(decision.kind) ?? []), decision.key])
  }
  return [...byKind].map(([kind, keys]) => {
    const values = new Set(keys.map((key) => preview.picks[key] ?? ''))
    return {
      kind,
      label: result.kindLabels[kind] ?? kind,
      keys,
      choices: result.choices[kind] ?? [],
      value: values.size === 1 ? [...values][0] : MIXED,
      manual: keys.filter((key) => preview.picks[key]).length,
    }
  })
})

function choose(keys: string[], choice: string) {
  history.change(() => preview.pick(keys, choice))
}
</script>

<template>
  <section :class="$style.menu">
    <div :class="$style.body">
      <p v-if="!preview.result" :class="$style.muted">Working out what these are…</p>
      <label v-for="group in groups" :key="group.kind" :class="$style.group">
        <span :class="$style.name">
          {{ group.label }} <span :class="$style.muted">({{ group.keys.length }})</span>
          <span v-if="group.manual" :class="$style.manual">manual</span>
        </span>
        <select
          :class="$style.select"
          :value="group.value"
          @change="choose(group.keys, ($event.target as HTMLSelectElement).value)"
        >
          <option v-if="group.value === MIXED" :value="MIXED" disabled>— mixed —</option>
          <option v-for="choice in group.choices" :key="choice.value" :value="choice.value">{{ choice.label }}</option>
        </select>
      </label>
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

.manual {
  padding: 0 4px;
  font-size: 10px;
  color: #1a1a1a;
  background: var(--accent);
  border-radius: 2px;
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
