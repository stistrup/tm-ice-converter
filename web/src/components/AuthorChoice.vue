<script setup lang="ts">
import { ref } from 'vue'
import { fetchAuthor, saveAuthor, type Author } from '../api'

const meAsAuthor = defineModel<boolean>({ required: true })

// entered here when there's none
const author = ref<Author | null>(null)
const editing = ref(false)
const draft = ref<Author>({ login: '', nickname: '', zone: '' })
const authorError = ref('')
// nickname and zone come from your own maps; typed only when none carries the ID
const typingName = ref(false)

const startEditing = () => {
  draft.value = { login: author.value?.login ?? '', nickname: '', zone: '' }
  authorError.value = ''
  typingName.value = false
  editing.value = true
}

const storeAuthor = async () => {
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

// as the dialog opens: the saved account, or none (then off)
const refresh = async () => {
  editing.value = false
  author.value = await fetchAuthor().catch(() => null)
  if (!author.value) {
    meAsAuthor.value = false
    draft.value = { login: '', nickname: '', zone: '' }
  }
}

defineExpose({ refresh })
</script>

<template>
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
</template>

<style module>

.field {
  composes: field from '../ui.module.css';
}

.option {
  composes: option from '../ui.module.css';
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
  composes: error from '../ui.module.css';
}

.button {
  composes: button from '../ui.module.css';
}

</style>
