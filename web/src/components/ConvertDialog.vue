<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { fetchOutputName, targetExists } from '../api'
import { useConvertStore } from '../stores/convert'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'
import AuthorChoice from './AuthorChoice.vue'
import ConvertBar from './ConvertBar.vue'
import ConvertProgress from './ConvertProgress.vue'
import FolderChoice from './FolderChoice.vue'

const store = useMapStore()
const preview = usePreviewStore()
const converting = useConvertStore()
const dialog = ref<HTMLDialogElement>()
const authorChoice = ref<InstanceType<typeof AuthorChoice>>()
const folderChoice = ref<InstanceType<typeof FolderChoice>>()
const outputName = ref('')
const targetFolder = ref('')

// the maps ticked in the list; with none (or Only the open map) it converts the open one
const chosen = computed(() => store.maps.filter((map) => store.chosenMaps.has(map.path)))
const onlyOpen = ref(false)
const batch = computed(() => chosen.value.length > 0 && !(onlyOpen.value && store.currentMap))
// each ticked map's default name without "(ICE)", by path; the suffix goes after it
const batchBases = ref<Record<string, string>>({})
const suffix = ref('(ICE)')
const batchNames = computed(() =>
  Object.fromEntries(Object.entries(batchBases.value).map(([path, base]) => [path, suffix.value.trim() ? `${base} ${suffix.value.trim()}` : base])),
)
const openLabel = computed(() =>
  chosen.value.length ? `Convert ${chosen.value.length} map${chosen.value.length === 1 ? '' : 's'}…` : 'Convert…',
)

// map files of those names already in the folder (by file name): converting replaces them only when ticked
const existing = ref<string[]>([])
const exists = computed(() => existing.value.length > 0)
const overwrite = ref(false)
let existsCall = 0
let existsTimer: ReturnType<typeof setTimeout> | undefined

const existsAs = async (name: string) => {
  try {
    return name.trim() !== '' && (await targetExists(targetFolder.value, name))
  } catch {
    return false
  }
}

watch([targetFolder, outputName, batch, () => JSON.stringify(batchNames.value)], () => {
  overwrite.value = false
  clearTimeout(existsTimer)
  existsTimer = setTimeout(async () => {
    const call = ++existsCall
    const names = batch.value ? chosen.value.map((map) => batchNames.value[map.path] ?? '') : [outputName.value]
    const found = await Promise.all(names.map(existsAs))
    if (call === existsCall) existing.value = names.filter((_, i) => found[i])
  }, 200)
})

const startError = ref('')
const job = computed(() => converting.job)
const running = computed(() => job.value?.state === 'running')
// the dialog shows the conversion (running or its result) instead of the form
const showingJob = computed(() => job.value !== null && converting.inDialog)
const canConvert = computed(
  () => !running.value && (batch.value || (store.currentMap !== null && outputName.value.trim().length > 0)) && (!exists.value || overwrite.value),
)

// one map with the settings the preview shows (the working ones), several with their own (drafts); it runs in the backend
const convert = async () => {
  const current = store.currentMap
  if (!canConvert.value || (!batch.value && !current)) return
  startError.value = ''
  try {
    await converting.start(batch.value ? '' : current!.path, {
      settings: batch.value ? null : preview.working,
      name: batch.value ? '' : outputName.value.trim(),
      maps: batch.value ? chosen.value.map((map) => map.path) : undefined,
      suffix: batch.value ? suffix.value.trim() : undefined,
      folder: targetFolder.value,
      fakeAuthorTime: fakeAuthorTime.value,
      meAsAuthor: meAsAuthor.value,
      rebuildBlocks: rebuildBlocks.value,
      overwrite: overwrite.value,
    })
    overwrite.value = false
    // converting is the commitment, when asked
    if (!batch.value && saveSettings.value) void preview.save()
  } catch (e) {
    startError.value = e instanceof Error ? e.message : String(e)
  }
}

const fakeAuthorTime = ref(false)
const meAsAuthor = ref(false)
const rebuildBlocks = ref(false)
// off by default: trying things out keeps the saved settings
const saveSettings = ref(false)

// the form; a finished conversion still shown is let go
const open = async () => {
  if (running.value) return showJob()
  if (job.value) void converting.dismiss()
  startError.value = ''
  const current = store.currentMap
  if (!current && !chosen.value.length) return
  onlyOpen.value = false
  saveSettings.value = false
  outputName.value = current ? `${(current.name ?? '').replace(/\s*\(ice[^)]*\)$/i, '')} (ICE)` : ''
  dialog.value?.showModal()
  if (current)
    fetchOutputName(current.path).then((name) => {
      if (store.currentMap === current) outputName.value = name
    }, () => undefined)
  batchBases.value = {}
  for (const map of chosen.value)
    fetchOutputName(map.path).then((name) => (batchBases.value[map.path] = name.replace(/ \(ICE\)$/, '')), () => undefined)
  await Promise.all([folderChoice.value?.refresh(), authorChoice.value?.refresh()])
}

const showJob = () => {
  converting.inDialog = true
  if (!dialog.value?.open) dialog.value?.showModal()
}

// while it runs, closing sends it to the corner bar
const background = () => {
  converting.inDialog = false
  dialog.value?.close()
}

// a finished one is let go with the dialog
const close = () => {
  if (running.value) return background()
  if (job.value && converting.inDialog) void converting.dismiss()
  dialog.value?.close()
}

// a click on the backdrop lands on the dialog itself
const onClick = (event: MouseEvent) => {
  if (event.target === dialog.value) close()
}

// Esc, or closed some other way
const onClosed = () => {
  if (running.value) converting.inDialog = false
}
</script>

<template>
  <div :class="$style.corner">
    <ConvertBar @show="showJob" />
    <button
      v-if="!(job?.state === 'running' && !converting.inDialog)"
      type="button"
      :class="$style.open"
      :disabled="!store.currentMap && !chosen.length"
      @click="open"
    >
      {{ openLabel }}
    </button>
  </div>
  <dialog ref="dialog" :class="$style.dialog" aria-labelledby="convert-title" @click="onClick" @close="onClosed">
    <ConvertProgress v-if="showingJob && job" @close="close" @background="background" />
    <form v-else method="dialog" :class="$style.form" @submit.prevent="convert">
      <header :class="$style.header">
        <span id="convert-title">{{ batch ? `Convert ${chosen.length} map${chosen.length === 1 ? '' : 's'}` : `Convert ${store.currentMap?.name}` }}</span>
      </header>
      <div :class="$style.body">
        <label v-if="chosen.length && store.currentMap" :class="$style.option">
          <input v-model="onlyOpen" type="checkbox" />
          <span>Only the open map ({{ store.currentMap.path }})</span>
        </label>
        <label v-if="batch" :class="$style.field">
          <span>Suffix</span>
          <input v-model="suffix" type="text" placeholder="Added after each map name" />
        </label>
        <div v-if="batch" :class="$style.field">
          <span>Maps</span>
          <ul :class="$style.batch">
            <li v-for="map in chosen" :key="map.path" :title="`Saved as ${batchNames[map.path] ?? '…'}`">
              <span :class="$style.grow">{{ map.path }}</span>
              <span v-if="existing.includes(batchNames[map.path] ?? '')" :class="$style.warn">exists</span>
            </li>
          </ul>
        </div>
        <label v-else :class="$style.field">
          <span>Map name</span>
          <input v-model="outputName" type="text" />
          <span :class="$style.muted">Same name, same map in game: a new name makes a new map (its own UID).</span>
        </label>
        <div :class="$style.checks">
          <label :class="$style.option">
            <input v-model="fakeAuthorTime" type="checkbox" />
            <span>Fake 1h author time</span>
          </label>
          <AuthorChoice ref="authorChoice" v-model="meAsAuthor" />
          <label :class="$style.option">
            <input v-model="rebuildBlocks" type="checkbox" />
            <span>DEV: Rebuild custom blocks</span>
            <span :class="$style.info" title="Write the custom blocks again even when their items didn't change" aria-label="What it does">i</span>
          </label>
        </div>
        <FolderChoice ref="folderChoice" v-model="targetFolder" />
        <div v-if="exists" :class="$style.exists">
          <p v-if="batch" :class="$style.warn">{{ targetFolder || 'Maps' }} already has {{ existing.length }} of them.</p>
          <p v-else :class="$style.warn">{{ targetFolder || 'Maps' }} already has “{{ outputName.trim() }}”.</p>
          <label :class="$style.option">
            <input v-model="overwrite" type="checkbox" />
            <span>{{ batch && existing.length > 1 ? 'Overwrite them' : 'Overwrite it' }}</span>
          </label>
        </div>
        <label v-if="!batch && preview.dirty" :class="$style.option">
          <input v-model="saveSettings" type="checkbox" />
          <span>Save these settings for this map</span>
        </label>
        <p v-if="startError" :class="$style.error" role="alert">{{ startError }}</p>
      </div>
      <footer :class="$style.actions">
        <button type="button" :class="$style.button" @click="close">Cancel</button>
        <button type="submit" :class="[$style.button, $style.primary]" :disabled="!canConvert" :title="!canConvert && exists && !overwrite ? 'tick Overwrite it first' : ''">
          Convert
        </button>
      </footer>
    </form>
  </dialog>
</template>

<style module>
/* the bar and the button side by side in the view's bottom right corner */
.corner {
  display: flex;
  align-items: flex-end;
}

.grow {
  flex: 1;
  min-width: 0;
}

.open {
  display: block;
  height: 40px;
  padding: 0 32px;
  font-size: 14px;
  font-weight: 600;
  color: #1a1a1a;
  cursor: default;
  background: var(--accent);
  border: 0;
  border-top: 1px solid var(--line);
  border-left: 1px solid var(--line);
  /* in the corner: only the inner corner rounds */
  border-radius: 4px 0 0;
}

.open:disabled {
  opacity: 0.5;
}

.open:hover:not(:disabled) {
  color: #000;
  background: #ffb866;
  box-shadow: inset 0 0 0 2px rgb(255 255 255 / 35%);
}

.open:active {
  background: #c97a22;
}

.dialog {
  width: min(380px, calc(100vw - 32px));
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

.form {
  margin: 0;
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
  gap: 10px;
  padding: 10px 8px;
}

.field {
  composes: field from '../ui.module.css';
}

.option {
  composes: option from '../ui.module.css';
}

.checks {
  display: grid;
  gap: 2px;
}

.error {
  composes: error from '../ui.module.css';
}

.info {
  composes: info from '../ui.module.css';
}

.batch {
  max-height: 160px;
  padding: 4px 6px;
  overflow-y: auto;
  background: var(--field);
  border: 1px solid var(--line);
  border-radius: 2px;
}

.batch li {
  display: flex;
  gap: 6px;
}

.exists {
  display: grid;
  gap: 4px;
  padding: 6px 8px;
  border: 1px solid var(--warn);
  border-radius: 2px;
}

.warn {
  margin: 0;
  color: var(--warn);
  overflow-wrap: anywhere;
}

.muted {
  composes: muted from '../ui.module.css';
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

.button.primary:hover:not(:disabled) {
  background: #ffb866;
}

.button.primary:disabled {
  opacity: 0.55;
}
</style>
