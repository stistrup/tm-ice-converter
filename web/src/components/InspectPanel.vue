<script setup lang="ts">
import { computed } from 'vue'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'
import { displayName, infoRows } from '../viewer/names'
import { outcomeLabel } from '../viewer/outcomes'
import TargetChoice from './TargetChoice.vue'
import PiecePicture from './PiecePicture.vue'

const store = useMapStore()
const preview = usePreviewStore()

const info = computed(() => (store.selected === null ? null : store.infos[store.selected]))

const becomes = computed(() => {
  const decision = store.selected === null ? undefined : preview.decisions?.[store.selected]
  return decision && { label: outcomeLabel(decision.outcome) + (decision.picked ? ' (target)' : ''), rule: decision.rule }
})

// instances come first in store.infos; boxes and cones have no pieces
const materials = computed(() => {
  const manifest = store.scene?.manifest
  if (!manifest || store.selected === null || store.selected >= manifest.instances.length) return null
  const { pieces } = manifest.meshes[manifest.instances[store.selected].mesh]
  return [...new Set(pieces.map((piece) => manifest.materials[piece.material].name))].sort()
})

// everything about the piece, one line each, behind the (i) by its name
const details = computed(() => {
  if (!info.value) return ''
  const lines = infoRows(info.value).map((row) => `${row.label}: ${row.value}`)
  if (materials.value) lines.push('', 'Materials:', ...materials.value.map((m) => `  ${m}`))
  return lines.join('\n')
})
</script>

<template>
  <aside v-if="info" :class="$style.panel">
    <header :class="$style.header">
      <span>Properties</span>
      <span :class="$style.count">{{ store.selection.length }} selected</span>
    </header>
    <PiecePicture />
    <div :class="$style.body">
      <h2 :class="$style.title">
        {{ displayName(info) }}
        <span :class="$style.info" :title="details" aria-label="Properties">i</span>
      </h2>
      <dl v-if="becomes" :class="$style.properties">
        <dt :title="becomes.rule ? `decided by rule ${becomes.rule}` : undefined" :class="{ [$style.hinted]: becomes.rule }">Becomes</dt>
        <dd>{{ becomes.label }}</dd>
      </dl>
      <TargetChoice :class="$style.manual" />
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
  height: 24px;
  padding: 0 8px;
  color: var(--strong);
  background: var(--header);
}

.body {
  min-height: 0;
  padding: 8px;
  overflow-y: auto;
}

.manual {
  margin-top: 8px;
}

.info {
  display: inline-grid;
  place-items: center;
  width: 13px;
  height: 13px;
  margin-left: 4px;
  vertical-align: 1px;
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

.properties .hinted {
  text-decoration: underline dotted;
  text-underline-offset: 2px;
  cursor: default;
}

.fold {
  display: flex;
  grid-column: 1 / -1;
  gap: 4px;
  align-items: center;
  margin-top: 2px;
  padding: 0;
  color: var(--muted);
  text-align: left;
  cursor: default;
  background: none;
  border: 0;
}

.fold:hover {
  color: var(--strong);
}

.arrow {
  flex: none;
  width: 10px;
  height: 10px;
  background: currentColor;
  clip-path: polygon(25% 10%, 80% 50%, 25% 90%);
}

.open {
  transform: rotate(90deg);
}

.count {
  color: var(--muted);
}

.properties .list {
  grid-column: 2;
}

.actions {
  display: flex;
  justify-content: flex-end;
  margin-top: 8px;
}

.button {
  height: 20px;
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
