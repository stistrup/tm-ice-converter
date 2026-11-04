<script setup lang="ts">
import { computed } from 'vue'
import { useMapStore } from '../stores/map'
import { displayName, infoRows } from '../viewer/names'
import PiecePicture from './PiecePicture.vue'

const store = useMapStore()

const info = computed(() => (store.selected === null ? null : store.infos[store.selected]))

const rows = computed(() => {
  if (!info.value || store.selected === null) return []
  const list = infoRows(info.value)
  const size = store.view?.sizeOf(store.selected)
  if (size) {
    const metres = [size.x, size.y, size.z].map((value) => Math.round(value * 10) / 10)
    list.splice(1, 0, { key: 'size', label: 'Size', value: `${metres.join(' × ')} m` })
  }
  return list
})

// instances come first in store.infos; boxes and cones have no pieces
const mesh = computed(() => {
  const manifest = store.scene?.manifest
  if (!manifest || store.selected === null || store.selected >= manifest.instances.length) return null
  const { pieces } = manifest.meshes[manifest.instances[store.selected].mesh]
  return {
    triangles: pieces.reduce((sum, piece) => sum + piece.indices / 3, 0),
    materials: [...new Set(pieces.map((piece) => manifest.materials[piece.material].name))].sort(),
  }
})
</script>

<template>
  <aside v-if="info" :class="$style.panel">
    <header :class="$style.header">Properties</header>
    <PiecePicture />
    <div :class="$style.body">
      <h2 :class="$style.title">{{ displayName(info) }}</h2>
      <dl :class="$style.properties">
        <template v-for="row in rows" :key="row.key">
          <dt>{{ row.label }}</dt>
          <dd>{{ row.value }}</dd>
        </template>
        <template v-if="mesh">
          <dt>Triangles</dt>
          <dd>{{ mesh.triangles }}</dd>
          <dt>Materials</dt>
          <dd>
            <div v-for="material in mesh.materials" :key="material">{{ material }}</div>
          </dd>
        </template>
      </dl>
      <button type="button" :class="$style.button" @click="store.view?.focus(store.selected!)">Focus</button>
    </div>
  </aside>
</template>

<style module>
.panel {
  position: absolute;
  top: 0;
  right: 0;
  display: flex;
  flex-direction: column;
  width: 290px;
  max-height: 100%;
  background: var(--panel);
  border-bottom: 1px solid var(--line);
  border-left: 1px solid var(--line);
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
  min-height: 0;
  padding: 8px;
  overflow-y: auto;
}

.title {
  margin: 0 0 6px;
  overflow-wrap: anywhere;
  font-size: 13px;
  font-weight: 600;
  color: var(--strong);
}

.properties {
  display: grid;
  grid-template-columns: 84px minmax(0, 1fr);
  gap: 2px 8px;
  margin: 0;
}

.properties dt {
  color: var(--muted);
}

.properties dd {
  margin: 0;
  overflow-wrap: anywhere;
  font-family: var(--mono);
  font-size: 11px;
  line-height: 16px;
}

.button {
  height: 20px;
  margin-top: 8px;
  padding: 0 10px;
  cursor: default;
  background: #3a3a3a;
  border: 1px solid var(--line);
  border-radius: 2px;
}

.button:hover {
  background: #454545;
}
</style>
