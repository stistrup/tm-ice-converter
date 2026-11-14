<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { GroupNode } from '../api'

const props = defineProps<{ tree: GroupNode[] }>()
// "" = all
const path = defineModel<string>({ required: true })

const open = ref(false)
const expanded = ref(new Set<string>())
const root = ref<HTMLElement>()

const labelOf = (wanted: string): string => {
  const trail: string[] = []
  const find = (nodes: GroupNode[]): boolean =>
    nodes.some((node) => {
      trail.push(node.label)
      if (node.path === wanted || find(node.children)) return true
      trail.pop()
      return false
    })
  return wanted === '' || !find(props.tree) ? 'All' : trail.join(' ▸ ')
}
const label = computed(() => labelOf(path.value))

// rows as shown: each level's "All" is the group itself
const rows = computed(() => {
  const list: { node: GroupNode; depth: number }[] = []
  const add = (nodes: GroupNode[], depth: number) => {
    for (const node of nodes) {
      list.push({ node, depth })
      if (expanded.value.has(node.path)) add(node.children, depth + 1)
    }
  }
  add(props.tree, 0)
  return list
})

const choose = (wanted: string) => {
  path.value = wanted
  open.value = false
}

const expand = (node: GroupNode) => {
  if (expanded.value.has(node.path)) expanded.value.delete(node.path)
  else expanded.value.add(node.path)
}

const outside = (event: PointerEvent) => {
  if (!root.value?.contains(event.target as Node)) open.value = false
}
watch(open, (now) => (now ? window.addEventListener('pointerdown', outside) : window.removeEventListener('pointerdown', outside)))
onBeforeUnmount(() => window.removeEventListener('pointerdown', outside))
</script>

<template>
  <div ref="root" :class="$style.picker">
    <button type="button" :class="$style.current" :aria-expanded="open" @click="open = !open">
      <span :class="$style.grow">{{ label }}</span>
      <span :class="[$style.chevron, { [$style.up]: open }]" />
    </button>
    <ul v-if="open" :class="$style.list" role="listbox">
      <li :class="[$style.row, { [$style.chosen]: path === '' }]" role="option" @click="choose('')">
        <span :class="$style.arrowSpace" />
        <span>All</span>
      </li>
      <li
        v-for="{ node, depth } in rows"
        :key="node.path"
        :class="[$style.row, { [$style.chosen]: path === node.path }]"
        :style="{ paddingLeft: `${4 + depth * 14}px` }"
        role="option"
        @click="choose(node.path)"
      >
        <button v-if="node.children.length" type="button" :class="$style.arrow" :aria-expanded="expanded.has(node.path)" @click.stop="expand(node)">
          <span :class="[$style.fold, { [$style.unfolded]: expanded.has(node.path) }]" />
        </button>
        <span v-else :class="$style.arrowSpace" />
        <span>{{ node.children.length ? `${node.label}: all` : node.label }}</span>
      </li>
    </ul>
  </div>
</template>

<style module>
.picker {
  position: relative;
  flex: 1;
  min-width: 0;
}

.current {
  composes: select from '../ui.module.css';
  display: flex;
  gap: 6px;
  align-items: center;
  width: 100%;
  text-align: left;
  cursor: default;
}

.list {
  position: absolute;
  z-index: 10;
  top: 100%;
  right: 0;
  left: 0;
  max-height: 260px;
  margin: 2px 0 0;
  padding: 2px 0;
  overflow-y: auto;
  list-style: none;
  background: var(--field);
  border: 1px solid var(--line);
  border-radius: 2px;
  box-shadow: 0 4px 12px rgb(0 0 0 / 40%);
}

.row {
  display: flex;
  gap: 4px;
  align-items: center;
  min-height: 20px;
  padding: 0 6px 0 4px;
  cursor: default;
}

.row:hover {
  background: var(--hover);
}

.chosen {
  color: var(--strong);
  background: var(--selected);
}

.arrow {
  display: grid;
  flex: none;
  place-items: center;
  width: 14px;
  height: 14px;
  padding: 0;
  background: none;
  border: 0;
}

.arrowSpace {
  flex: none;
  width: 14px;
}

.chevron {
  composes: chevron from '../ui.module.css';
}

/* points right; down when unfolded */
.fold {
  width: 6px;
  height: 6px;
  border-right: 1.5px solid var(--muted);
  border-bottom: 1.5px solid var(--muted);
  transform: rotate(-45deg);
}

.unfolded {
  transform: translateY(-1px) rotate(45deg);
}

.up {
  composes: up from '../ui.module.css';
}

.grow {
  composes: grow from '../ui.module.css';
}
</style>
