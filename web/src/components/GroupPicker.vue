<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { GroupNode } from '../api'

// placeholder: shown while the path is none of the tree's
const props = defineProps<{ tree: GroupNode[]; placeholder?: string }>()
// "" = all
const path = defineModel<string>({ required: true })

const open = ref(false)
const root = ref<HTMLElement>()
const menus = ref<HTMLElement>()
// the menus live on the page, so the scrolling panel can't clip them
const at = ref({ top: 0, left: 0, width: 0 })
// opened sub-menus, one per level; each opens left of the one before
const chain = ref<{ node: GroupNode; top: number; right: number }[]>([])

const labelOf = (wanted: string): string => {
  const trail: string[] = []
  const find = (nodes: GroupNode[]): boolean =>
    nodes.some((node) => {
      trail.push(node.label)
      if (node.path === wanted || find(node.children)) return true
      trail.pop()
      return false
    })
  return wanted === '' ? 'All' : !find(props.tree) ? (props.placeholder ?? 'All') : trail.join(' ▸ ')
}
const label = computed(() => labelOf(path.value))

// within the chosen path: marks the rows that lead to it
const onPath = (node: GroupNode) => path.value === node.path || path.value.startsWith(`${node.path}/`)

const choose = (wanted: string) => {
  path.value = wanted
  open.value = false
}

// hovering a row opens its sub-menu (closing deeper ones); a leaf only closes them
const enter = (node: GroupNode, level: number, event: Event) => {
  if (chain.value[level]?.node === node) return
  const kept = chain.value.slice(0, level)
  if (node.children.length) {
    const row = (event.currentTarget as HTMLElement).getBoundingClientRect()
    const menu = (event.currentTarget as HTMLElement).closest('ul')!.getBoundingClientRect()
    kept.push({ node, top: row.top - 3, right: window.innerWidth - menu.left - 1 })
  }
  chain.value = kept
}

const click = (node: GroupNode, level: number, event: Event) => {
  if (node.children.length) enter(node, level, event)
  else choose(node.path)
}

const outside = (event: Event) => {
  const target = event.target as Node
  if (!root.value?.contains(target) && !menus.value?.contains(target)) open.value = false
}
const close = (event: Event) => {
  if (!menus.value?.contains(event.target as Node)) open.value = false
}
const listen = (on: boolean) => {
  const method = on ? 'addEventListener' : 'removeEventListener'
  window[method]('pointerdown', outside)
  window[method]('resize', close)
  // the panel scrolling would leave them behind
  window[method]('scroll', close, true)
}
watch(open, (now) => {
  chain.value = []
  if (now) {
    const box = root.value!.getBoundingClientRect()
    at.value = { top: box.bottom + 2, left: box.left, width: box.width }
  }
  listen(now)
})
onBeforeUnmount(() => listen(false))
</script>

<template>
  <div ref="root" :class="$style.picker">
    <button type="button" :class="$style.current" :aria-expanded="open" @click="open = !open">
      <span :class="$style.grow">{{ label }}</span>
      <span :class="[$style.chevron, { [$style.up]: open }]" />
    </button>
    <Teleport to="body">
      <div v-if="open" ref="menus">
        <ul
          :class="$style.menu"
          :style="{ top: `${at.top}px`, left: `${at.left}px`, minWidth: `${at.width}px` }"
          role="menu"
        >
          <li :class="[$style.row, { [$style.chosen]: path === '' }]" role="menuitem" @mouseenter="chain = []" @click="choose('')">
            <span :class="$style.arrowSpace" />
            <span>All</span>
          </li>
          <li
            v-for="node in tree"
            :key="node.path"
            :class="[$style.row, { [$style.chosen]: path === node.path, [$style.opened]: chain[0]?.node === node, [$style.trail]: onPath(node) }]"
            role="menuitem"
            :aria-haspopup="node.children.length > 0"
            @mouseenter="enter(node, 0, $event)"
            @click="click(node, 0, $event)"
          >
            <span :class="node.children.length ? $style.arrow : $style.arrowSpace" />
            <span>{{ node.label }}</span>
          </li>
        </ul>
        <ul
          v-for="(sub, level) in chain"
          :key="sub.node.path"
          :class="$style.menu"
          :style="{ top: `${sub.top}px`, right: `${sub.right}px` }"
          role="menu"
        >
          <li :class="[$style.row, { [$style.chosen]: path === sub.node.path }]" role="menuitem" @mouseenter="chain = chain.slice(0, level + 1)" @click="choose(sub.node.path)">
            <span :class="$style.arrowSpace" />
            <span>All</span>
          </li>
          <li
            v-for="node in sub.node.children"
            :key="node.path"
            :class="[$style.row, { [$style.chosen]: path === node.path, [$style.opened]: chain[level + 1]?.node === node, [$style.trail]: onPath(node) }]"
            role="menuitem"
            :aria-haspopup="node.children.length > 0"
            @mouseenter="enter(node, level + 1, $event)"
            @click="click(node, level + 1, $event)"
          >
            <span :class="node.children.length ? $style.arrow : $style.arrowSpace" />
            <span>{{ node.label }}</span>
          </li>
        </ul>
      </div>
    </Teleport>
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

.menu {
  position: fixed;
  z-index: 10;
  min-width: 120px;
  max-height: 320px;
  margin: 0;
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
  padding: 0 10px 0 4px;
  white-space: nowrap;
  cursor: default;
}

.row:hover,
.opened {
  background: var(--hover);
}

.trail {
  color: var(--strong);
}

.chosen {
  color: var(--strong);
  background: var(--selected);
}

/* points left: the sub-menu opens that way */
.arrow {
  flex: none;
  width: 14px;
  height: 10px;
  background: var(--muted);
  clip-path: polygon(70% 10%, 25% 50%, 70% 90%);
}

.arrowSpace {
  flex: none;
  width: 14px;
}

.chevron {
  composes: chevron from '../ui.module.css';
}

.up {
  composes: up from '../ui.module.css';
}

.grow {
  composes: grow from '../ui.module.css';
}
</style>
