<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { inGroup } from '../affected'
import { fetchGroups, type GroupNode } from '../api'
import { useHistoryStore } from '../stores/history'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'
import GroupPicker from './GroupPicker.vue'

const store = useMapStore()
const preview = usePreviewStore()
const history = useHistoryStore()

const tree = ref<GroupNode[] | null>(null)
onMounted(async () => {
  tree.value = (await fetchGroups().catch(() => null))?.tree ?? null
})

// groups come with a preview
watch(
  () => !!preview.state && !preview.result && !preview.loading,
  (needed) => {
    if (needed) void preview.run()
  },
  { immediate: true },
)

// only groups with pieces on this map: every group path and its parents
const shown = computed(() => {
  if (!tree.value || !preview.decisions) return null
  const present = new Set<string>()
  for (const d of preview.decisions) {
    const parts = d?.group?.split('/') ?? []
    parts.forEach((_, i) => present.add(parts.slice(0, i + 1).join('/')))
  }
  const prune = (nodes: GroupNode[]): GroupNode[] =>
    nodes.filter((n) => present.has(n.path)).map((n) => ({ ...n, children: prune(n.children) }))
  return prune(tree.value)
})

const NONE = '\u0000'
const path = ref(NONE)
let made: number[] | null = null

const select = (wanted: string) => {
  const picked = (preview.decisions ?? []).flatMap((d, i) => (d && inGroup(d.group, wanted) ? [i] : []))
  history.change(() => (store.selection = made = picked))
  path.value = wanted
}

// another selection: the picker no longer names it
watch(
  () => store.selection,
  (now) => {
    if (now !== made) path.value = NONE
  },
)
</script>

<template>
  <div v-if="shown" :class="$style.row">
    <GroupPicker
      :model-value="path"
      :tree="shown"
      placeholder="Select group"
      :class="$style.grow"
      @update:model-value="select"
    />
  </div>
</template>

<style module>
.row {
  display: flex;
  gap: 6px;
  align-items: center;
  margin-bottom: 8px;
}

.grow {
  composes: grow from '../ui.module.css';
}
</style>
