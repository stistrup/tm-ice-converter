<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import type { MapEntry } from '../api'
import { readJson, writeJson } from '../storage'
import { useMapStore } from '../stores/map'

interface Folder {
  path: string
  name: string
  folders: Map<string, Folder>
  maps: MapEntry[]
  // every map under it that the search lets through
  all: MapEntry[]
}

type Row =
  | { kind: 'folder'; key: string; folder: Folder; depth: number; open: boolean }
  | { kind: 'map'; key: string; map: MapEntry; depth: number }

const STORAGE_KEY = 'open-map-folders'

const store = useMapStore()
const search = ref('')
const openFolders = ref(new Set<string>(readJson<string[]>(STORAGE_KEY, [])))
const shown = defineModel<boolean>('shown', { default: true })

watch(
  openFolders,
  (open) => writeJson(STORAGE_KEY, [...open]),
  { deep: true },
)

const treeBox = ref<HTMLElement>()

const openFoldersOf = (map: MapEntry | null) => {
  let path = ''
  for (const part of map?.folder.split(/[\\/]/).filter(Boolean) ?? []) {
    path += `/${part}`
    openFolders.value.add(path)
  }
}

// a reopened map's row has to be visible
watch(() => store.currentMap, openFoldersOf)

// the title over the view asks for the current map: folders open, search cleared if it hides it, row scrolled to
watch(
  () => store.revealed,
  async () => {
    const map = store.currentMap
    if (!map) return
    openFoldersOf(map)
    if (needle.value && !`${map.folder} ${map.name}`.toLowerCase().includes(needle.value)) search.value = ''
    await nextTick()
    treeBox.value?.querySelector(`[data-map="${CSS.escape(map.path)}"]`)?.scrollIntoView({ block: 'center' })
  },
)

const needle = computed(() => search.value.trim().toLowerCase())

const tree = computed(() => {
  const root: Folder = { path: '', name: '', folders: new Map(), maps: [], all: [] }
  for (const map of store.maps) {
    if (needle.value && !`${map.folder} ${map.name}`.toLowerCase().includes(needle.value)) continue
    let folder = root
    folder.all.push(map)
    for (const part of map.folder.split(/[\\/]/).filter(Boolean)) {
      let next = folder.folders.get(part)
      if (!next) {
        next = { path: `${folder.path}/${part}`, name: part, folders: new Map(), maps: [], all: [] }
        folder.folders.set(part, next)
      }
      next.all.push(map)
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

const toggle = (path: string) => {
  if (needle.value) return
  if (openFolders.value.has(path)) openFolders.value.delete(path)
  else openFolders.value.add(path)
}

const chosen = computed(() => store.maps.filter((map) => store.chosenMaps.has(map.path)).length)

// ticked maps under each folder, by its path
const chosenUnder = computed(() => {
  const counts = new Map<string, number>()
  const count = (folder: Folder) => {
    counts.set(folder.path, folder.all.filter((map) => store.chosenMaps.has(map.path)).length)
    for (const child of folder.folders.values()) count(child)
  }
  count(tree.value)
  return counts
})

const refreshing = ref(false)
const refresh = async () => {
  refreshing.value = true
  await store.loadMaps()
  refreshing.value = false
}

const checked = (event: Event) => (event.target as HTMLInputElement).checked
const indent = (depth: number) => ({ paddingLeft: `${6 + depth * 12}px` })
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
      <div :class="$style.chosen">
        <span>{{ chosen }} selected</span>
        <span :class="$style.actions">
          <button type="button" :class="[$style.clear, $style.icon]" :disabled="refreshing" title="Refresh maps" aria-label="Refresh maps" @click="refresh">
            <svg viewBox="0 0 16 16" width="12" height="12" aria-hidden="true">
              <path d="M13.5 8A5.5 5.5 0 1 1 11.9 4.1M12.5 1.5v3h-3" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </button>
          <button type="button" :class="$style.clear" :disabled="!chosen" @click="store.chosenMaps.clear()">Clear</button>
        </span>
      </div>
    </div>
    <p v-if="shown && store.mapsError" :class="$style.error">{{ store.mapsError }}</p>
    <div v-if="shown" ref="treeBox" :class="$style.tree">
      <template v-for="row in rows" :key="row.key">
        <div
          v-if="row.kind === 'folder'"
          :class="[$style.row, $style.folder]"
          :style="indent(row.depth)"
        >
          <button type="button" :class="$style.label" :aria-expanded="row.open" @click="toggle(row.folder.path)">
            <span :class="[$style.arrow, { [$style.open]: row.open }]" />
            <span :class="$style.name">{{ row.folder.name }}</span>
            <span :class="$style.count">{{ row.folder.all.length }}</span>
          </button>
          <input
            type="checkbox"
            :class="$style.tick"
            :checked="chosenUnder.get(row.folder.path) === row.folder.all.length"
            :indeterminate="(chosenUnder.get(row.folder.path) ?? 0) > 0 && (chosenUnder.get(row.folder.path) ?? 0) < row.folder.all.length"
            :aria-label="`Select maps in ${row.folder.name}`"
            @change="store.choose(row.folder.all.map((map) => map.path), checked($event))"
          />
        </div>
        <div
          v-else
          :data-map="row.map.path"
          :class="[$style.row, { [$style.current]: row.map.path === store.currentMap?.path }]"
          :style="indent(row.depth)"
        >
          <button type="button" :class="$style.label" :title="row.map.path" @click="row.map.path === store.currentMap?.path ? store.closeMap() : store.openMap(row.map)">
            <span :class="$style.spacer" />
            <span :class="$style.name">{{ row.map.name }}</span>
          </button>
          <input
            type="checkbox"
            :class="$style.tick"
            :checked="store.chosenMaps.has(row.map.path)"
            :aria-label="`Select ${row.map.name}`"
            @change="store.choose([row.map.path], checked($event))"
          />
        </div>
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
  composes: chevron from '../ui.module.css';
}

.chevron.open {
  transform: translateY(2px) rotate(-135deg);
}

.search {
  flex: none;
  padding: 4px;
}

.chosen {
  display: flex;
  align-items: center;
  justify-content: space-between;
  height: 20px;
  padding: 2px 2px 0;
  color: var(--muted);
}

.clear {
  padding: 0 4px;
  color: var(--text);
  cursor: default;
  background: none;
  border: 0;
}

.actions {
  display: flex;
  align-items: center;
}

.icon svg {
  display: block;
}

.clear:hover:not(:disabled) {
  color: var(--strong);
}

.clear:disabled {
  color: var(--muted);
  opacity: 0.5;
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
}

.tick {
  flex: none;
  width: 13px;
  height: 13px;
  margin: 0;
  padding: 0;
  accent-color: var(--accent);
}

.label {
  display: flex;
  flex: 1;
  gap: 4px;
  align-items: center;
  min-width: 0;
  height: 100%;
  padding: 0 6px 0 0;
  color: inherit;
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
