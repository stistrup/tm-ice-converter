<script setup lang="ts">
import { computed, ref } from 'vue'
import { useHistoryStore } from '../stores/history'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'
import { normalKey } from '../api'

const store = useMapStore()
const preview = usePreviewStore()
const history = useHistoryStore()

const open = ref(false)

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

const hover = (index: number | null) => {
  store.view?.showHover(index)
  if (index !== null) store.view?.focus(index)
}

const remove = (key: string) => {
  hover(null)
  history.change(() => preview.pick([key], ''))
}
</script>

<template>
  <div :class="$style.picks">
    <button type="button" :class="$style.fold" :aria-expanded="open" :disabled="!picks.length" @click="open = !open">
      <span>Conversion targets: {{ picks.length }}</span>
      <span v-if="picks.length" :class="[$style.chevron, { [$style.up]: open }]" />
    </button>
    <ul v-if="open && picks.length" :class="$style.list">
      <li
        v-for="pick in picks"
        :key="pick.key"
        :class="[$style.pick, { [$style.gone]: pick.index === null }]"
        :title="pick.index === null ? `${pick.key}: not on this map` : pick.key"
        @mouseenter="hover(pick.index)"
        @mouseleave="hover(null)"
      >
        <span :class="$style.grow">{{ pick.name }}</span>
        <span :class="$style.choice">{{ pick.label }}</span>
        <button type="button" :class="$style.textButton" @click="remove(pick.key)">Remove</button>
      </li>
    </ul>
  </div>
</template>

<style module>
.picks {
  margin-top: 8px;
}

.list {
  display: grid;
  max-height: 220px;
  margin: 2px 0 0;
  padding: 0;
  overflow-y: auto;
  list-style: none;
  border: 1px solid var(--line);
  border-radius: 2px;
}

.pick {
  display: flex;
  gap: 6px;
  align-items: center;
  min-height: 20px;
  padding: 0 2px 0 6px;
}

.pick:hover {
  background: var(--field);
}

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

.textButton {
  composes: textButton from '../ui.module.css';
}
</style>
