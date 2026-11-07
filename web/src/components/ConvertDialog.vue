<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { fetchAuthor, fetchFolders, fetchOutputName, saveAuthor, targetExists, type Author, type FolderList } from '../api'
import { useConvertStore } from '../stores/convert'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'

const store = useMapStore()
const preview = usePreviewStore()
const converting = useConvertStore()
const dialog = ref<HTMLDialogElement>()
const outputName = ref('')
// picked from the folders in Maps, or a new one typed
const folderMode = ref<'type' | 'pick'>('pick')
const typedFolder = ref('')
const pickedFolder = ref('')
const list = ref<FolderList | null>(null)
const folders = computed(() => list.value?.folders ?? [])
const targetFolder = computed(() => (folderMode.value === 'pick' ? pickedFolder.value : typedFolder.value).trim())

// a map file of that name already in the folder (by file name): converting replaces it only when ticked
const exists = ref(false)
const overwrite = ref(false)
let existsCall = 0
let existsTimer: ReturnType<typeof setTimeout> | undefined

watch([targetFolder, outputName], () => {
  overwrite.value = false
  clearTimeout(existsTimer)
  existsTimer = setTimeout(async () => {
    const call = ++existsCall
    let found = false
    try {
      found = await targetExists(targetFolder.value, outputName.value)
    } catch {
      found = false
    }
    if (call === existsCall) exists.value = found
  }, 200)
})

const startError = ref('')
const job = computed(() => converting.job)
const running = computed(() => job.value?.state === 'running')
// the dialog shows the conversion (running or its result) instead of the form
const showingJob = computed(() => job.value !== null && converting.inDialog)
const canConvert = computed(() => !running.value && outputName.value.trim().length > 0 && (!exists.value || overwrite.value))

// with the settings the preview shows (the working ones); it runs in the backend
async function convert() {
  const current = store.currentMap
  if (!current || !canConvert.value) return
  startError.value = ''
  try {
    await converting.start(current.path, {
      settings: preview.working,
      name: outputName.value.trim(),
      folder: targetFolder.value,
      fakeAuthorTime: fakeAuthorTime.value,
      meAsAuthor: meAsAuthor.value,
      rebuildBlocks: rebuildBlocks.value,
      overwrite: overwrite.value,
    })
    overwrite.value = false
  } catch (e) {
    startError.value = e instanceof Error ? e.message : String(e)
  }
}

// per step: done, running (with its count) or still to come
function stepState(index: number): 'done' | 'now' | 'todo' {
  const step = job.value?.progress.step ?? -1
  if (job.value?.state === 'done' || index < step) return 'done'
  return index === step && job.value?.state === 'running' ? 'now' : 'todo'
}

const percent = computed(() => Math.round(job.value?.state === 'done' ? 100 : (job.value?.progress.percent ?? 0)))
const stepNow = computed(() => {
  const p = job.value?.progress
  if (!p || p.step < 0 || p.step >= p.stepNames.length) return p?.detail ?? 'Starting'
  return `${p.stepNames[p.step]}${p.total ? ` ${p.done}/${p.total}` : ''}`
})

const fakeAuthorTime = ref(false)
const meAsAuthor = ref(false)
const rebuildBlocks = ref(false)
// the account "Me as author" puts on the map; entered here when there's none
const author = ref<Author | null>(null)
const editing = ref(false)
const draft = ref<Author>({ login: '', nickname: '', zone: '' })
const authorError = ref('')

// nickname and zone come from your own maps; typed only when none carries the ID
const typingName = ref(false)

function startEditing() {
  draft.value = { login: author.value?.login ?? '', nickname: '', zone: '' }
  authorError.value = ''
  typingName.value = false
  editing.value = true
}

async function storeAuthor() {
  authorError.value = ''
  try {
    author.value = await saveAuthor({ login: draft.value.login.trim(), nickname: draft.value.nickname.trim(), zone: draft.value.zone.trim() })
    editing.value = false
  } catch (e) {
    authorError.value = e instanceof Error ? e.message : String(e)
    if (!typingName.value) {
      typingName.value = true
      authorError.value += ': type the nickname and zone'
    }
  }
}

function useFolder(folder: string) {
  if (folders.value.includes(folder)) {
    folderMode.value = 'pick'
    pickedFolder.value = folder
  } else {
    folderMode.value = 'type'
    typedFolder.value = folder
  }
}

// the form; a finished conversion still shown is let go
async function open() {
  if (running.value) return showJob()
  if (job.value) void converting.dismiss()
  startError.value = ''
  const current = store.currentMap
  if (!current) return
  outputName.value = `${(current.name ?? '').replace(/\s*\(ice[^)]*\)$/i, '')} (ICE)`
  dialog.value?.showModal()
  fetchOutputName(current.path).then((name) => {
    if (store.currentMap === current) outputName.value = name
  }, () => undefined)
  editing.value = false
  const [folderList, account] = await Promise.allSettled([fetchFolders(), fetchAuthor()])
  list.value = folderList.status === 'fulfilled' ? folderList.value : null
  author.value = account.status === 'fulfilled' ? account.value : null
  if (!author.value) {
    meAsAuthor.value = false
    draft.value = { login: '', nickname: '', zone: '' }
  }
  useFolder(list.value?.progress ?? '')
}

function showJob() {
  converting.inDialog = true
  if (!dialog.value?.open) dialog.value?.showModal()
}

// while it runs, closing sends it to the corner bar
function background() {
  converting.inDialog = false
  dialog.value?.close()
}

// a finished one is let go with the dialog
function close() {
  if (running.value) return background()
  if (job.value && converting.inDialog) void converting.dismiss()
  dialog.value?.close()
}

// a click on the backdrop lands on the dialog itself
function onClick(event: MouseEvent) {
  if (event.target === dialog.value) close()
}

// Esc, or closed some other way
function onClosed() {
  if (running.value) converting.inDialog = false
}
</script>

<template>
  <div :class="$style.corner">
    <div v-if="job && !converting.inDialog" :class="[$style.bar, $style[job.state]]">
      <button type="button" :class="$style.barMain" :title="`Show the conversion of ${job.name}`" @click="showJob">
        <span :class="$style.barText">
          <template v-if="job.state === 'running'">Converting {{ job.name }} · {{ stepNow }}</template>
          <template v-else-if="job.state === 'done'">Converted {{ job.name }}</template>
          <template v-else>Conversion of {{ job.name }} failed</template>
        </span>
        <span v-if="job.state === 'running'" :class="$style.barPercent">{{ percent }}%</span>
        <span :class="$style.barTrack" aria-hidden="true"><span :class="$style.barFill" :style="{ width: `${percent}%` }" /></span>
      </button>
      <button v-if="job.state !== 'running'" type="button" :class="$style.barClose" aria-label="Dismiss" @click="converting.dismiss()">×</button>
    </div>
    <button type="button" :class="$style.open" :disabled="!store.currentMap" @click="open">Convert…</button>
  </div>
  <dialog ref="dialog" :class="$style.dialog" aria-labelledby="convert-title" @click="onClick" @close="onClosed">
    <div v-if="showingJob && job" :class="$style.form">
      <header :class="$style.header">
        <span id="convert-title">
          {{ job.state === 'running' ? 'Converting' : job.state === 'done' ? 'Converted' : 'Conversion failed:' }} {{ job.name }}
        </span>
      </header>
      <div :class="$style.body">
        <ol :class="$style.steps">
          <li v-for="(name, i) in job.progress.stepNames" :key="name" :class="$style[stepState(i)]">
            <span :class="$style.mark" aria-hidden="true">{{ stepState(i) === 'done' ? '✓' : stepState(i) === 'now' ? '›' : '' }}</span>
            <span :class="$style.grow">{{ name }}</span>
            <span v-if="stepState(i) === 'now' && job.progress.total" :class="$style.count">{{ job.progress.done }}/{{ job.progress.total }}</span>
            <span v-if="stepState(i) === 'now' && job.progress.detail" :class="$style.muted">{{ job.progress.detail }}</span>
          </li>
        </ol>
        <p v-if="job.progress.step < 0 && job.progress.detail" :class="$style.muted">{{ job.progress.detail }}…</p>
        <div :class="$style.overall">
          <span :class="$style.track" aria-hidden="true"><span :class="$style.fill" :style="{ width: `${percent}%` }" /></span>
          <span :class="$style.count">{{ percent }}%</span>
        </div>
        <div v-if="job.outcome" :class="$style.result" role="status">
          <p :class="$style.ok">Saved: {{ job.outcome.output.startsWith('/') ? '' : 'Maps/' }}{{ job.outcome.output }}</p>
          <p :class="$style.muted">{{ job.outcome.summary }}</p>
          <p v-for="problem in job.outcome.problems" :key="problem" :class="$style.warn">Left out: {{ problem }}</p>
          <p v-if="job.outcome.failures.length" :class="$style.warn" :title="job.outcome.failures.join('\n')">
            {{ job.outcome.failures.length }} piece(s) failed to build, see the report next to the map
          </p>
          <p v-if="job.outcome.missingFiles" :class="$style.warn">
            {{ job.outcome.missingFiles }} game files aren't extracted: in game, Openplanet › Extract block meshes, then convert again
          </p>
          <p :class="$style.muted">Don't forget to calculate shadows.</p>
        </div>
        <p v-if="job.error" :class="$style.error" role="alert">{{ job.error }}</p>
      </div>
      <footer :class="$style.actions">
        <button v-if="running" type="button" :class="$style.button" @click="background">Continue in background</button>
        <button v-else type="button" :class="$style.button" @click="close">Close</button>
      </footer>
    </div>
    <form v-else method="dialog" :class="$style.form" @submit.prevent="convert">
      <header :class="$style.header">
        <span id="convert-title">Convert {{ store.currentMap?.name }}</span>
      </header>
      <div :class="$style.body">
        <label :class="$style.field">
          <span>Map name</span>
          <input v-model="outputName" type="text" />
          <span :class="$style.muted">Same name, same map in game: a new name makes a new map (its own UID).</span>
        </label>
        <div :class="$style.checks">
          <label :class="$style.option">
            <input v-model="fakeAuthorTime" type="checkbox" />
            <span>Fake 1h author time</span>
          </label>
          <div :class="$style.option">
            <label :class="$style.option">
              <input v-model="meAsAuthor" type="checkbox" :disabled="!author" />
              <span>{{ author ? `Me (${author.nickname}) as author` : 'Me as author' }}</span>
            </label>
            <button v-if="!editing" type="button" :class="$style.link" @click="startEditing">{{ author ? 'Change' : 'Set user ID' }}</button>
          </div>
          <div v-if="editing" :class="$style.author">
            <label :class="$style.field">
              <span>User ID</span>
              <input v-model="draft.login" type="text" placeholder="22 characters, as on trackmania.io" />
            </label>
            <label v-if="typingName" :class="$style.field">
              <span>Nickname</span>
              <input v-model="draft.nickname" type="text" />
            </label>
            <label v-if="typingName" :class="$style.field">
              <span>Zone</span>
              <input v-model="draft.zone" type="text" placeholder="World|Europe|Sweden" />
            </label>
            <p v-if="authorError" :class="$style.error">{{ authorError }}</p>
            <div :class="$style.authorActions">
              <button type="button" :class="$style.button" @click="editing = false">Cancel</button>
              <button type="button" :class="$style.button" :disabled="!draft.login.trim() || (typingName && !draft.nickname.trim())" @click="storeAuthor">Save</button>
            </div>
          </div>
          <label :class="$style.option">
            <input v-model="rebuildBlocks" type="checkbox" />
            <span>Rebuild custom blocks</span>
          </label>
        </div>
        <fieldset :class="$style.field">
          <legend>Save to</legend>
          <label :class="$style.option">
            <input v-model="folderMode" type="radio" value="pick" :disabled="!folders.length" />
            <span>Existing folder</span>
          </label>
          <select v-model="pickedFolder" :class="$style.select" :disabled="folderMode !== 'pick'" aria-label="Existing folder">
            <option v-for="folder in folders" :key="folder" :value="folder">{{ folder }}</option>
          </select>
          <label :class="$style.option">
            <input v-model="folderMode" type="radio" value="type" />
            <span>New folder</span>
          </label>
          <input
            v-model="typedFolder"
            type="text"
            placeholder="Saved in Maps (leave empty to not save in a folder)"
            :disabled="folderMode !== 'type'"
            aria-label="New folder"
          />
        </fieldset>
        <div v-if="exists" :class="$style.exists">
          <p :class="$style.warn">{{ targetFolder || 'Maps' }} already has “{{ outputName.trim() }}”.</p>
          <label :class="$style.option">
            <input v-model="overwrite" type="checkbox" />
            <span>Overwrite it</span>
          </label>
        </div>
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

.bar {
  display: flex;
  align-items: stretch;
  width: 380px;
  max-width: calc(100vw - 200px);
  height: 32px;
  margin-right: 6px;
  background: var(--panel);
  border: 1px solid var(--line);
  border-bottom: 0;
  border-radius: 3px 3px 0 0;
}

.barMain {
  position: relative;
  display: flex;
  flex: 1;
  gap: 8px;
  align-items: center;
  min-width: 0;
  padding: 0 10px 4px;
  color: var(--text);
  text-align: left;
  cursor: default;
  background: none;
  border: 0;
}

.barMain:hover {
  background: var(--hover);
}

.barText {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.barPercent {
  flex: none;
  color: var(--strong);
}

.barTrack {
  position: absolute;
  right: 0;
  bottom: 0;
  left: 0;
  height: 4px;
  background: var(--field);
}

.barFill {
  display: block;
  height: 100%;
  background: var(--accent);
  transition: width 0.3s;
}

.done .barFill {
  background: #6cbf6c;
}

.failed .barFill {
  width: 100% !important;
  background: var(--error);
}

.failed .barText {
  color: var(--error);
}

.barClose {
  flex: none;
  width: 28px;
  color: var(--muted);
  cursor: default;
  background: none;
  border: 0;
  border-left: 1px solid var(--line);
}

.barClose:hover {
  color: var(--strong);
  background: var(--hover);
}

.steps {
  display: grid;
  gap: 2px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.steps li {
  display: flex;
  gap: 6px;
  align-items: center;
  min-height: 20px;
}

.mark {
  flex: none;
  width: 12px;
  text-align: center;
}

.steps .done {
  color: var(--text);
}

.steps .done .mark {
  color: #6cbf6c;
}

.steps .now {
  color: var(--strong);
  font-weight: 600;
}

.steps .now .mark {
  color: var(--accent);
}

.steps .todo {
  color: var(--muted);
}

.grow {
  flex: 1;
  min-width: 0;
}

.count {
  flex: none;
  color: var(--muted);
  font-weight: 400;
}

.overall {
  display: flex;
  gap: 8px;
  align-items: center;
}

.track {
  flex: 1;
  height: 8px;
  overflow: hidden;
  background: var(--field);
  border: 1px solid var(--line);
  border-radius: 4px;
}

.fill {
  display: block;
  height: 100%;
  background: var(--accent);
  transition: width 0.3s;
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
  display: grid;
  gap: 3px;
  min-width: 0;
  margin: 0;
  padding: 0;
  border: 0;
}

.field legend {
  margin-bottom: 3px;
  padding: 0;
}

.option {
  display: flex;
  gap: 6px;
  align-items: center;
  min-height: 18px;
  cursor: default;
}

.option input {
  flex: none;
  width: auto;
  height: auto;
  margin: 0;
}

.select {
  height: 22px;
  padding: 0 4px;
  color: var(--text);
  background: var(--field);
  border: 1px solid var(--line);
  border-radius: 2px;
}

.select:disabled,
.field input[type='text']:disabled {
  opacity: 0.5;
}

.checks {
  display: grid;
  gap: 2px;
}

/* a disabled option reads as off: its label greyed with it */
.option:has(> input:disabled) {
  color: var(--muted);
  opacity: 0.5;
}

.author {
  display: grid;
  gap: 6px;
  margin: 2px 0 4px 20px;
  padding: 6px 8px;
  border: 1px solid var(--line);
  border-radius: 2px;
}

.authorActions {
  display: flex;
  gap: 6px;
  justify-content: flex-end;
}

.link {
  padding: 0;
  color: var(--muted);
  text-decoration: underline;
  cursor: default;
  background: none;
  border: 0;
}

.link:hover {
  color: var(--strong);
}

.error {
  margin: 0;
  color: var(--error);
}

.status {
  display: flex;
  gap: 6px;
  align-items: center;
  color: var(--muted);
}

.spinner {
  flex: none;
  width: 10px;
  height: 10px;
  border: 1.5px solid currentColor;
  border-right-color: transparent;
  border-radius: 50%;
  animation: spin 0.7s linear infinite;
}

@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}

.result {
  display: grid;
  gap: 3px;
}

.result p {
  margin: 0;
  overflow-wrap: anywhere;
}

.ok {
  color: var(--strong);
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
  margin: 0;
  color: var(--muted);
}

.actions {
  display: flex;
  gap: 6px;
  justify-content: flex-end;
  padding: 0 8px 8px;
}

.button {
  height: 22px;
  padding: 0 12px;
  cursor: default;
  background: #3a3a3a;
  border: 1px solid var(--line);
  border-radius: 2px;
}

.button:hover:not(:disabled) {
  background: #454545;
}

.primary,
.primary:disabled {
  font-weight: 600;
  color: #1a1a1a;
  background: var(--accent);
}

.primary:disabled {
  opacity: 0.55;
}
</style>
