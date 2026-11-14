<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'

// a list that needs more room than its spot: on the page next to the anchor, toward the larger side of the window
const props = defineProps<{ anchor: HTMLElement | undefined; title: string }>()
const open = defineModel<boolean>({ required: true })

const panel = ref<HTMLElement>()
const at = ref<Record<string, string>>({})

const place = () => {
  const box = props.anchor!.getBoundingClientRect()
  const [w, h] = [window.innerWidth, window.innerHeight]
  at.value = {
    ...(box.top > h / 2 ? { bottom: `${h - box.top + 4}px` } : { top: `${box.bottom + 4}px` }),
    ...(box.left > w / 2 ? { right: `${w - box.right}px` } : { left: `${box.left}px` }),
  }
}

const outside = (event: Event) => {
  const target = event.target as Node
  if (!props.anchor?.contains(target) && !panel.value?.contains(target)) open.value = false
}
const close = () => (open.value = false)
const listen = (on: boolean) => {
  const method = on ? 'addEventListener' : 'removeEventListener'
  window[method]('pointerdown', outside)
  window[method]('resize', close)
}
watch(
  open,
  (now) => {
    if (now) place()
    listen(now)
  },
  { immediate: true },
)
onBeforeUnmount(() => listen(false))
</script>

<template>
  <Teleport to="body">
    <section v-if="open" ref="panel" :class="$style.panel" :style="at">
      <header :class="$style.header">
        <span :class="$style.grow">{{ title }}</span>
        <button type="button" :class="$style.close" aria-label="Close" @click="close">×</button>
      </header>
      <ul :class="$style.list">
        <slot />
      </ul>
    </section>
  </Teleport>
</template>

<style module>
.panel {
  position: fixed;
  z-index: 10;
  display: flex;
  flex-direction: column;
  width: 460px;
  max-width: calc(100vw - 20px);
  max-height: 70vh;
  background: var(--panel);
  border: 1px solid var(--line);
  border-radius: 2px;
  box-shadow: 0 6px 18px rgb(0 0 0 / 45%);
}

.header {
  display: flex;
  flex: none;
  align-items: center;
  height: 24px;
  padding: 0 2px 0 8px;
  color: var(--strong);
  background: var(--header);
}

.close {
  width: 20px;
  height: 20px;
  padding: 0;
  font-size: 15px;
  line-height: 1;
  color: var(--muted);
  cursor: default;
  background: none;
  border: 0;
}

.close:hover {
  color: var(--strong);
}

.list {
  display: grid;
  align-content: start;
  min-height: 0;
  margin: 0;
  padding: 4px 0;
  overflow-y: auto;
  list-style: none;
}

/* rows come from the slot */
.list > li {
  display: flex;
  gap: 6px;
  align-items: center;
  min-height: 20px;
  padding: 0 4px 0 8px;
  white-space: nowrap;
}

.list > li:hover {
  background: var(--field);
}

.grow {
  composes: grow from '../ui.module.css';
}
</style>
