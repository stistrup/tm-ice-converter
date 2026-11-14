<script setup lang="ts">
// a list row's eye: hovering shows the piece in the view, a click keeps the view there (handlers from useHoverFocus)
defineEmits<{ enter: []; leave: []; keep: [] }>()
</script>

<template>
  <button
    type="button"
    :class="$style.eye"
    title="Show in view; click to stay there"
    aria-label="Show in view"
    @mouseenter="$emit('enter')"
    @mouseleave="$emit('leave')"
    @click.stop="$emit('keep')"
  >
    <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true">
      <path d="M1.5 8S4 3.5 8 3.5 14.5 8 14.5 8 12 12.5 8 12.5 1.5 8 1.5 8Z" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round" />
      <circle cx="8" cy="8" r="2" fill="currentColor" />
    </svg>
  </button>
</template>

<style module>
.eye {
  display: grid;
  flex: none;
  place-items: center;
  width: 18px;
  height: 18px;
  padding: 0;
  color: var(--muted);
  cursor: default;
  background: none;
  border: 0;
  border-radius: 2px;
}

.eye:hover {
  color: var(--accent);
  background: var(--line);
}
</style>
