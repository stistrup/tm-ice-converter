<script setup lang="ts">
import { computed, ref } from 'vue'
import { useMapStore } from '../stores/map'
import { categoryLabels } from '../viewer/categories'

const store = useMapStore()
const search = ref('')

const selectedInfo = computed(() => (store.selected.length === 1 ? store.infos[store.selected[0]] : null))
const selectedRows = computed(() =>
  Object.entries(selectedInfo.value ?? {})
    .filter(([key]) => key !== 'placeholder')
    .map(([key, value]) => ({ key, value: Array.isArray(value) ? value.join(', ') : String(value) })),
)
const names = computed(() => {
  const needle = search.value.trim().toLowerCase()
  return needle ? store.pieceNames.filter((n) => n.name.toLowerCase().includes(needle)) : store.pieceNames
})

function selectAll(indices: number[]) {
  store.selected = indices
  store.focusRequest++
}
</script>

<template>
  <aside :class="$style.panel">
    <p v-if="!store.scene" :class="$style.empty">Details show here once a map is open.</p>
    <template v-else>
      <section :class="$style.selection">
        <h2 :class="$style.heading">{{ store.scene.manifest.map }}</h2>
        <p v-if="store.selected.length === 0" :class="$style.muted">Click a piece to see what it is.</p>
        <p v-else-if="store.selected.length > 1" :class="$style.muted">
          {{ store.selected.length }} pieces selected: {{ store.infos[store.selected[0]]?.name }}
        </p>
        <dl v-else :class="$style.details">
          <template v-for="row in selectedRows" :key="row.key">
            <dt>{{ row.key }}</dt>
            <dd>{{ row.value }}</dd>
          </template>
        </dl>
        <div v-if="store.selected.length" :class="$style.actions">
          <button type="button" :class="$style.button" @click="store.focusRequest++">Focus</button>
          <button type="button" :class="$style.button" @click="store.selected = []">Clear</button>
        </div>
      </section>

      <section :class="$style.names">
        <input v-model="search" type="search" placeholder="Filter by name" aria-label="Filter pieces by name" />
        <ul :class="$style.nameList">
          <li v-for="entry in names" :key="entry.name">
            <button type="button" :class="$style.name" :title="entry.name" @click="selectAll(entry.indices)">
              <span :class="$style.label">{{ entry.name }}</span>
              <span :class="$style.meta">{{ categoryLabels[entry.category] }} · {{ entry.indices.length }}</span>
            </button>
          </li>
        </ul>
      </section>
    </template>
  </aside>
</template>

<style module>
.panel {
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--panel);
  border-left: 1px solid var(--line);
}

.empty,
.muted {
  margin: 0;
  color: var(--muted);
}

.empty {
  padding: 12px;
}

.selection {
  max-height: 50%;
  padding: 12px;
  overflow-y: auto;
  border-bottom: 1px solid var(--line);
}

.heading {
  margin: 0 0 8px;
  font-size: 14px;
  font-weight: 600;
}

.details {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  gap: 2px 10px;
  margin: 0;
}

.details dt {
  color: var(--muted);
}

.details dd {
  margin: 0;
  overflow-wrap: anywhere;
}

.actions {
  display: flex;
  gap: 6px;
  margin-top: 10px;
}

.button {
  padding: 2px 8px;
  cursor: pointer;
  background: var(--panel-raised);
  border: 1px solid var(--line);
  border-radius: 4px;
}

.button:hover {
  border-color: var(--accent);
}

.names {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  padding: 12px 0 0;
}

.names > input {
  width: auto;
  margin: 0 12px 8px;
}

.nameList {
  flex: 1;
  min-height: 0;
  margin: 0;
  padding: 0;
  overflow-y: auto;
  list-style: none;
}

.name {
  display: flex;
  gap: 8px;
  justify-content: space-between;
  width: 100%;
  padding: 3px 12px;
  text-align: left;
  cursor: pointer;
  background: none;
  border: 0;
}

.name:hover {
  background: var(--panel-raised);
}

.label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.meta {
  flex: none;
  font-size: 11px;
  color: var(--muted);
}
</style>
