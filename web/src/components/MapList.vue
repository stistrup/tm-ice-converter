<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { MapEntry } from '../api'
import { useMapStore } from '../stores/map'

interface Folder {
  path: string
  name: string
  folders: Map<string, Folder>
  maps: MapEntry[]
  count: number
}

type Row =
  | { kind: 'folder'; key: string; folder: Folder; depth: number; open: boolean }
  | { kind: 'map'; key: string; map: MapEntry; depth: number }

const STORAGE_KEY = 'open-map-folders'

const store = useMapStore()
const search = ref('')
const openFolders = ref(new Set<string>(readOpenFolders()))
const shown = defineModel<boolean>('shown', { default: true })

function readOpenFolders(): string[] {
  try {
    return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '[]')
  } catch {
    return []
  }
}

watch(
  openFolders,
  (open) => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify([...open]))
    } catch {
      // only a convenience
    }
  },
  { deep: true },
)

// a reopened map's row has to be visible
watch(
  () => store.currentMap,
  (map) => {
    let path = ''
    for (const part of map?.folder.split(/[\\/]/).filter(Boolean) ?? []) {
      path += `/${part}`
      openFolders.value.add(path)
    }
  },
)

const needle = computed(() => search.value.trim().toLowerCase())

const tree = computed(() => {
  const root: Folder = { path: '', name: '', folders: new Map(), maps: [], count: 0 }
  for (const map of store.maps) {
    if (needle.value && !`${map.folder} ${map.name}`.toLowerCase().includes(needle.value)) continue
    let folder = root
    folder.count++
    for (const part of map.folder.split(/[\\/]/).filter(Boolean)) {
      let next = folder.folders.get(part)
      if (!next) {
        next = { path: `${folder.path}/${part}`, name: part, folders: new Map(), maps: [], count: 0 }
        folder.folders.set(part, next)
      }
      next.count++
      folder = next
    }
    folder.maps.push(map)
  }
  return root
})

const rows = computed(() => {
  const list: Row[] = []
  const add = (folder: Folder, depth: number) => {
    for (const child of [...folder.folders.values()].sort((a, b) => a.name.localeCompare(b.name))) {
      const open = Boolean(needle.value) || openFolders.value.has(child.path)
      list.push({ kind: 'folder', key: `f:${child.path}`, folder: child, depth, open })
      if (open) add(child, depth + 1)
    }
    for (const map of folder.maps) list.push({ kind: 'map', key: `m:${map.path}`, map, depth })
  }
  add(tree.value, 0)
  return list
})

function toggle(path: string) {
  if (needle.value) return
  if (openFolders.value.has(path)) openFolders.value.delete(path)
  else openFolders.value.add(path)
}

function indent(depth: number) {
  return { paddingLeft: `${6 + depth * 12}px` }
}
</script>

<template>
  <aside :class="$style.panel">
    <button
      type="button"
      :class="$style.header"
      :aria-expanded="shown"
      :title="shown ? 'Hide maps' : 'Show maps'"
      @click="shown = !shown"
    >
      <span>Maps</span>
      <span :class="[$style.chevron, { [$style.open]: shown }]" />
    </button>
    <div v-if="shown" :class="$style.search">
      <input v-model="search" type="search" placeholder="Search" aria-label="Search maps" />
    </div>
    <p v-if="shown && store.mapsError" :class="$style.error">{{ store.mapsError }}</p>
    <div v-if="shown" :class="$style.tree">
      <template v-for="row in rows" :key="row.key">
        <button
          v-if="row.kind === 'folder'"
          type="button"
          :class="[$style.row, $style.folder]"
          :style="indent(row.depth)"
          :aria-expanded="row.open"
          @click="toggle(row.folder.path)"
        >
          <span :class="[$style.arrow, { [$style.open]: row.open }]" />
          <span :class="$style.name">{{ row.folder.name }}</span>
          <span :class="$style.count">{{ row.folder.count }}</span>
        </button>
        <button
          v-else
          type="button"
          :class="[$style.row, { [$style.current]: row.map.path === store.currentMap?.path }]"
          :style="indent(row.depth)"
          :title="row.map.path"
          @click="store.openMap(row.map)"
        >
          <span :class="$style.spacer" />
          <span :class="$style.name">{{ row.map.name }}</span>
        </button>
      </template>
      <p v-if="store.maps.length && !rows.length" :class="$style.empty">No matches</p>
    </div>
  </aside>
</template>

<style module>
.panel {
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--panel);
}

.header {
  display: flex;
  flex: none;
  align-items: center;
  justify-content: space-between;
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

.chevron.open {
  transform: translateY(2px) rotate(-135deg);
}

.search {
  flex: none;
  padding: 4px;
}

.tree {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.row {
  display: flex;
  gap: 4px;
  align-items: center;
  width: 100%;
  height: 21px;
  padding-right: 8px;
  text-align: left;
  cursor: default;
  background: none;
  border: 0;
}

.row:hover {
  background: var(--hover);
}

.folder {
  color: var(--strong);
}

.current,
.current:hover {
  color: var(--strong);
  background: var(--selected);
}

.arrow,
.spacer {
  flex: none;
  width: 10px;
}

.arrow {
  height: 10px;
  background: var(--muted);
  clip-path: polygon(25% 10%, 80% 50%, 25% 90%);
}

.open {
  transform: rotate(90deg);
}

.name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.count {
  flex: none;
  color: var(--muted);
}

.error,
.empty {
  margin: 0;
  padding: 6px 8px;
  color: var(--muted);
}

.error {
  color: var(--error);
}
</style>
