<script setup lang="ts">
import { computed, ref } from 'vue'
import { useHistoryStore } from '../stores/history'
import { usePreviewStore } from '../stores/preview'
import ConfirmDialog from './ConfirmDialog.vue'

// the open map's settings next to its title: unsaved changes, save, discard, back to the original map
const preview = usePreviewStore()
const history = useHistoryStore()

const asking = ref<'save' | 'discard' | 'original' | null>(null)
const shown = (which: 'save' | 'discard' | 'original') =>
  computed({ get: () => asking.value === which, set: (on) => (asking.value = on ? which : null) })
const saveOpen = shown('save')
const discardOpen = shown('discard')
const originalOpen = shown('original')

const targets = computed(() => Object.keys(preview.working.pieces ?? {}).length)
const anything = computed(() => Object.keys(preview.working).length > 0)

const toOriginal = () => history.change(() => (preview.working = {}))
</script>

<template>
  <div v-if="preview.state && (preview.dirty || anything)" :class="$style.actions">
    <span v-if="preview.dirty" :class="$style.unsaved">Unsaved changes *</span>
    <button v-if="anything" type="button" :class="$style.action" @click="asking = 'original'">Revert to original</button>
    <button v-if="preview.dirty" type="button" :class="$style.action" @click="asking = 'discard'">Discard changes</button>
    <button v-if="preview.dirty" type="button" :class="$style.action" @click="asking = 'save'">Save</button>
  </div>

  <ConfirmDialog v-model="saveOpen" title="Save" confirm="Save" @confirmed="preview.save()">
    <p>Save this map's settings and conversion targets.</p>
  </ConfirmDialog>
  <ConfirmDialog v-model="discardOpen" title="Discard changes" confirm="Discard" @confirmed="history.revert()">
    <p>Go back to this map's last save.</p>
    <p>You can still undo (Ctrl + Z).</p>
  </ConfirmDialog>
  <ConfirmDialog v-model="originalOpen" title="Revert to original" confirm="Revert" @confirmed="toOriginal()">
    <p>Take away every setting and conversion target{{ targets ? ` (${targets})` : '' }}.</p>
    <p>Not saved until you save.</p>
  </ConfirmDialog>
</template>

<style module>
.actions {
  display: flex;
  gap: 2px;
  align-items: center;
  height: 24px;
  padding: 0 6px;
  background: var(--header);
  border-right: 1px solid var(--line);
}

.unsaved {
  padding: 0 6px;
  color: var(--accent);
  white-space: nowrap;
}

.action {
  height: 18px;
  padding: 0 6px;
  color: var(--text);
  white-space: nowrap;
  cursor: default;
  background: none;
  border: 0;
  border-radius: 2px;
}

.action:hover {
  color: var(--strong);
  background: var(--line);
}
</style>
