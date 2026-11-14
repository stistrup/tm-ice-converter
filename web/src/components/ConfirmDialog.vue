<script setup lang="ts">
import { ref, watch } from 'vue'

// a modal question: the slot explains, the confirm button does it
defineProps<{ title: string; confirm: string }>()
const open = defineModel<boolean>({ required: true })
const emit = defineEmits<{ confirmed: [] }>()

const dialog = ref<HTMLDialogElement>()

watch(open, (now) => {
  if (now && !dialog.value?.open) dialog.value?.showModal()
  if (!now && dialog.value?.open) dialog.value.close()
})

const yes = () => {
  emit('confirmed')
  open.value = false
}

// a click on the backdrop closes it
const onClick = (event: MouseEvent) => {
  if (event.target === dialog.value) open.value = false
}
</script>

<template>
  <dialog ref="dialog" :class="$style.dialog" @click="onClick" @close="open = false">
    <header :class="$style.header">{{ title }}</header>
    <div :class="$style.body"><slot /></div>
    <footer :class="$style.actions">
      <button type="button" :class="$style.button" @click="open = false">Cancel</button>
      <button type="button" :class="[$style.button, $style.primary]" autofocus @click="yes">{{ confirm }}</button>
    </footer>
  </dialog>
</template>

<style module>
.dialog {
  width: min(360px, calc(100vw - 32px));
  padding: 0;
  color: var(--text);
  background: var(--panel);
  border: 1px solid var(--line);
  border-radius: 3px;
  box-shadow: 0 8px 24px rgb(0 0 0 / 50%);
}

.dialog::backdrop {
  background: rgb(0 0 0 / 40%);
}

.header {
  display: flex;
  align-items: center;
  height: 24px;
  padding: 0 8px;
  color: var(--strong);
  background: var(--header);
}

.body {
  display: grid;
  gap: 6px;
  padding: 10px 8px;
}

.body p {
  margin: 0;
}

.actions {
  display: flex;
  gap: 6px;
  justify-content: flex-end;
  padding: 0 8px 8px;
}

.button {
  composes: button from '../ui.module.css';
}

/* over ui.module.css's button, whatever order the sheets load in */
.button.primary {
  font-weight: 600;
  color: #1a1a1a;
  background: var(--accent);
}

.button.primary:hover {
  background: #ffb866;
}
</style>
