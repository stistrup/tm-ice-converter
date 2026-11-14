<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useHistoryStore } from '../stores/history'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'
import { normalKey } from '../api'
import FloatingPanel from './FloatingPanel.vue'

const store = useMapStore()
const preview = usePreviewStore()
const history = useHistoryStore()

const open = ref(false)
const button = ref<HTMLElement>()

// index null: not on this map
const picks = computed(() => {
  const at = new Map<string, number>()
  preview.decisions?.forEach((d, i) => {
    if (d?.key) at.set(normalKey(d.key), i)
  })
  const result = preview.result
  return Object.entries(preview.picks).map(([key, choice]) => {
    const index = at.get(normalKey(key)) ?? null
    const kind = index === null ? undefined : preview.decisions?.[index]?.kind
    const label = (kind && result?.choices[kind]?.find((c) => c.value === choice)?.label) || choice
    const name = /^\S+/.exec(key)?.[0] ?? key
    return { key, name, label, index }
  })
})

// as a click in the view: one undo step
const select = (index: number | null) => {
  if (index !== null) history.change(() => (store.selected = index))
}

const hover = (index: number | null) => {
  store.view?.showHover(index)
  if (index !== null) store.view?.focus(index)
}

const remove = (key: string) => {
  hover(null)
  history.change(() => preview.pick([key], ''))
}

watch(open, (now) => now || hover(null))
watch(() => picks.value.length, (n) => n || (open.value = false))
</script>

<template>
  <button ref="button" type="button" :class="$style.fold" :aria-expanded="open" :disabled="!picks.length" @click="open = !open">
    <span>Conversion targets: {{ picks.length }}</span>
    <span v-if="picks.length" :class="[$style.chevron, { [$style.up]: open }]" />
  </button>
  <FloatingPanel v-model="open" :anchor="button" :title="`Conversion targets: ${picks.length}`">
    <li
      v-for="pick in picks"
      :key="pick.key"
      :class="{ [$style.gone]: pick.index === null }"
      :title="pick.index === null ? `${pick.key}: not on this map` : pick.key"
      @mouseenter="hover(pick.index)"
      @click="select(pick.index)"
      @mouseleave="hover(null)"
    >
      <span :class="$style.grow">{{ pick.name }}</span>
      <span :class="$style.choice">{{ pick.label }}</span>
      <button type="button" :class="$style.remove" @click.stop="remove(pick.key)">Remove</button>
    </li>
  </FloatingPanel>
</template>

<style module>
.gone {
  opacity: 0.5;
}

.choice {
  flex: none;
  color: var(--muted);
}

.fold {
  composes: fold from '../ui.module.css';
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

.remove {
  flex: none;
  height: 18px;
  padding: 0 6px;
  color: var(--accent);
  cursor: default;
  background: none;
  border: 0;
  border-radius: 2px;
}

.remove:hover {
  color: #ff3b30;
}
</style>
