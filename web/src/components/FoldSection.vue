<script setup lang="ts">
import { remembered } from '../storage'

const props = defineProps<{ title: string; id: string }>()
const open = remembered(`fold-${props.id}`, true)
</script>

<template>
  <section :class="$style.section">
    <button type="button" :class="$style.header" :aria-expanded="open" @click="open = !open">
      <span :class="$style.grow">{{ title }}</span>
      <slot name="side" />
      <span :class="[$style.chevron, { [$style.up]: open }]" />
    </button>
    <div v-if="open" :class="$style.body">
      <slot />
    </div>
  </section>
</template>

<style module>
.section {
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--panel);
}

.header {
  display: flex;
  flex: none;
  gap: 8px;
  align-items: center;
  width: 100%;
  height: 24px;
  padding: 0 8px;
  color: var(--strong);
  text-align: left;
  cursor: default;
  background: var(--header);
  border: 0;
}

.body {
  min-height: 0;
  padding: 8px;
}

.grow {
  composes: grow from '../ui.module.css';
}

.chevron {
  composes: chevron from '../ui.module.css';
}

.up {
  composes: up from '../ui.module.css';
}
</style>
