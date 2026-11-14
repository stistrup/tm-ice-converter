<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { inGroup, useAffected, useApplying } from '../affected'
import { fetchGroups, type GroupNode, type Groups } from '../api'
import { useHistoryStore } from '../stores/history'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'
import GroupPicker from './GroupPicker.vue'

const store = useMapStore()
const preview = usePreviewStore()
const history = useHistoryStore()

const groups = ref<Groups | null>(null)
onMounted(async () => {
  groups.value = await fetchGroups().catch(() => null)
})

const path = ref('')
const to = ref<'theme' | 'original'>('theme')
const method = ref('')
const ticked = ref(new Set<string>())

const node = computed(() => {
  const find = (nodes: GroupNode[]): GroupNode | null => {
    for (const n of nodes) {
      if (n.path === path.value) return n
      const inner = find(n.children)
      if (inner) return inner
    }
    return null
  }
  return groups.value ? find(groups.value.tree) : null
})

// "All" only takes targets away: no one method fits every group
const methods = computed(() => node.value?.methods ?? [])
watch(methods, (now) => {
  if (!now.includes(method.value)) method.value = now[0] ?? ''
  if (!now.length) to.value = 'original'
  ticked.value = new Set()
}, { immediate: true })
const toggles = computed(() => (node.value?.toggles ?? []).filter((t) => t.from === method.value))

const chosen = computed(() => {
  if (to.value === 'original') return 'original'
  return toggles.value.find((t) => ticked.value.has(t.label))?.to ?? method.value
})

const tick = (label: string, on: boolean) => {
  const next = new Set(ticked.value)
  if (on) next.add(label)
  else next.delete(label)
  ticked.value = next
}

const { affected, selectedByUs, toggle, keys } = useAffected(() => (d) => inGroup(d.group, path.value))
const { applying, status, run } = useApplying()

const apply = () => {
  const only = keys()
  if (chosen.value && only.length) void run(() => history.group(path.value, chosen.value, only))
}
</script>

<template>
  <div v-if="groups" :class="$style.conversion">
    <div :class="$style.row">
      <span :class="$style.title">Group</span>
      <GroupPicker v-model="path" :tree="groups.tree" />
      <span :class="$style.muted">to</span>
      <select v-model="to" :class="$style.select" aria-label="Material">
        <option value="theme" :disabled="!methods.length">Ice</option>
        <option value="original">Original</option>
      </select>
    </div>
    <div v-if="to === 'theme' && methods.length" :class="$style.row">
      <span :class="$style.title">As</span>
      <select v-model="method" :class="[$style.select, $style.grow]" aria-label="How">
        <option v-for="m in methods" :key="m" :value="m">{{ groups.methods[m] ?? m }}</option>
      </select>
    </div>
    <label v-for="t in to === 'theme' ? toggles : []" :key="t.label" :class="[$style.option, $style.indented]">
      <input type="checkbox" :checked="ticked.has(t.label)" @change="tick(t.label, ($event.target as HTMLInputElement).checked)" />
      <span>{{ t.label }}</span>
    </label>
    <div :class="$style.actions">
      <span :class="[$style.muted, $style.grow]">{{ status }}</span>
      <button
        type="button"
        :class="$style.smallButton"
        :disabled="applying || (!affected.length && !selectedByUs)"
        :title="preview.result ? '' : 'Waits for the preview'"
        @click="toggle"
      >
        {{ selectedByUs ? 'Deselect' : 'Select affected blocks' }}
      </button>
      <button
        type="button"
        :class="$style.smallButton"
        :disabled="applying || !store.selection.length || !preview.result"
        :title="!store.selection.length ? 'Select blocks first' : ''"
        @click="apply"
      >
        Apply
      </button>
    </div>
  </div>
</template>

<style module>
.conversion {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 4px;
  margin-bottom: 8px;
}

.row {
  display: flex;
  gap: 6px;
  align-items: center;
}

.actions {
  composes: row;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.title {
  flex: none;
  width: 44px;
}

.indented {
  padding-left: 50px;
}

.select {
  composes: select from '../ui.module.css';
}

.option {
  composes: option from '../ui.module.css';
}

.muted {
  composes: muted from '../ui.module.css';
}

.grow {
  composes: grow from '../ui.module.css';
}

.smallButton {
  composes: smallButton from '../ui.module.css';
}
</style>
