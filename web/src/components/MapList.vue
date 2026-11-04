<script setup lang="ts">
import { computed, ref } from 'vue'
import type { MapEntry } from '../api'
import { useMapStore } from '../stores/map'

const store = useMapStore()
const search = ref('')

const folders = computed(() => {
  const needle = search.value.trim().toLowerCase()
  const groups = new Map<string, MapEntry[]>()
  for (const map of store.maps) {
    if (needle && !`${map.folder} ${map.name}`.toLowerCase().includes(needle)) continue
    const group = groups.get(map.folder)
    if (group) group.push(map)
    else groups.set(map.folder, [map])
  }
  return [...groups].map(([name, maps]) => ({ name: name || 'Maps', maps }))
})
</script>

<template>
  <aside :class="$style.panel">
    <header :class="$style.header">
      <h1 :class="$style.title">Ice Converter</h1>
      <input v-model="search" type="search" placeholder="Search maps" aria-label="Search maps" />
    </header>
    <p v-if="store.mapsError" :class="$style.error">{{ store.mapsError }}</p>
    <div :class="$style.list">
      <section v-for="folder in folders" :key="folder.name">
        <h2 :class="$style.folder">{{ folder.name }}</h2>
        <button
          v-for="map in folder.maps"
          :key="map.path"
          type="button"
          :class="[$style.map, { [$style.current]: map.path === store.currentMap?.path }]"
          :title="map.path"
          @click="store.openMap(map)"
        >
          {{ map.name }}
        </button>
      </section>
      <p v-if="store.maps.length && !folders.length" :class="$style.empty">No maps match.</p>
    </div>
  </aside>
</template>

<style module>
.panel {
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--panel);
  border-right: 1px solid var(--line);
}

.header {
  padding: 12px;
  border-bottom: 1px solid var(--line);
}

.title {
  margin: 0 0 10px;
  font-size: 15px;
  font-weight: 600;
  color: var(--accent);
}

.list {
  flex: 1;
  min-height: 0;
  padding-bottom: 12px;
  overflow-y: auto;
}

.folder {
  position: sticky;
  top: 0;
  margin: 0;
  padding: 10px 12px 4px;
  font-size: 11px;
  font-weight: 600;
  color: var(--muted);
  background: var(--panel);
}

.map {
  display: block;
  width: 100%;
  padding: 4px 12px;
  overflow: hidden;
  text-align: left;
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: pointer;
  background: none;
  border: 0;
}

.map:hover {
  background: var(--panel-raised);
}

.current,
.current:hover {
  background: var(--accent-dim);
}

.error,
.empty {
  margin: 0;
  padding: 12px;
  color: var(--muted);
}

.error {
  color: var(--error);
}
</style>
