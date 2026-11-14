<script setup lang="ts" generic="T extends string | number">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'

export interface DropOption<V> {
  value: V
  label: string
  title?: string
  disabled?: boolean
}

// narrow: sized to its label, not the row
const props = defineProps<{ options: DropOption<T>[]; disabled?: boolean; placeholder?: string; narrow?: boolean }>()
const value = defineModel<T>({ required: true })

const open = ref(false)
const root = ref<HTMLElement>()
const menu = ref<HTMLElement>()
// the menu lives on the page, so a scrolling panel can't clip it
const at = ref({ top: 0, left: 0, width: 0 })
// a modal dialog covers the page: in one, the menu stays inside it
const inDialog = ref(false)
onMounted(() => (inDialog.value = !!root.value?.closest('dialog')))

const current = computed(() => props.options.find((o) => o.value === value.value))

const choose = (option: DropOption<T>) => {
  if (option.disabled) return
  value.value = option.value
  open.value = false
}

const outside = (event: Event) => {
  const target = event.target as Node
  if (!root.value?.contains(target) && !menu.value?.contains(target)) open.value = false
}
const close = (event: Event) => {
  if (!menu.value?.contains(event.target as Node)) open.value = false
}
const listen = (on: boolean) => {
  const method = on ? 'addEventListener' : 'removeEventListener'
  window[method]('pointerdown', outside)
  window[method]('resize', close)
  window[method]('scroll', close, true)
}
watch(open, (now) => {
  if (now) {
    const box = root.value!.getBoundingClientRect()
    at.value = { top: box.bottom + 2, left: box.left, width: box.width }
  }
  listen(now)
})
watch(
  () => props.disabled,
  (off) => {
    if (off) open.value = false
  },
)
onBeforeUnmount(() => listen(false))
</script>

<template>
  <div ref="root" :class="[$style.picker, { [$style.narrow]: narrow }]">
    <button
      type="button"
      :class="$style.current"
      :disabled="disabled"
      :aria-expanded="open"
      :title="current?.title"
      @click="open = !open"
    >
      <span :class="$style.grow">{{ current?.label ?? placeholder ?? '' }}</span>
      <span :class="[$style.chevron, { [$style.up]: open }]" />
    </button>
    <Teleport to="body" :disabled="inDialog">
      <ul
        v-if="open"
        ref="menu"
        :class="$style.menu"
        :style="{ top: `${at.top}px`, left: `${at.left}px`, minWidth: `${at.width}px` }"
        role="listbox"
      >
        <li
          v-for="option in options"
          :key="option.value"
          :class="[$style.row, { [$style.chosen]: option.value === value, [$style.off]: option.disabled }]"
          role="option"
          :aria-selected="option.value === value"
          :aria-disabled="option.disabled"
          :title="option.title"
          @click="choose(option)"
        >
          {{ option.label }}
        </li>
      </ul>
    </Teleport>
  </div>
</template>

<style module>
.picker {
  position: relative;
  flex: 1;
  min-width: 0;
}

.narrow {
  flex: none;
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
  align-items: center;
  min-height: 20px;
  padding: 0 10px 0 22px;
  white-space: nowrap;
  cursor: default;
}

.row:hover:not(.off) {
  background: var(--hover);
}

.chosen {
  color: var(--strong);
  background: var(--selected);
}

.off {
  color: var(--muted);
  opacity: 0.5;
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
