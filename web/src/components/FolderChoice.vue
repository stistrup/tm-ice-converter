<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { fetchFolders, type FolderList } from '../api'

// "" = Maps itself
const target = defineModel<string>({ required: true })

const folderMode = ref<'type' | 'pick'>('pick')
const typedFolder = ref('')
const pickedFolder = ref('')
const list = ref<FolderList | null>(null)
const folders = computed(() => list.value?.folders ?? [])

watch([folderMode, typedFolder, pickedFolder], () => {
  target.value = (folderMode.value === 'pick' ? pickedFolder.value : typedFolder.value).trim()
})

const useFolder = (folder: string) => {
  if (folders.value.includes(folder)) {
    folderMode.value = 'pick'
    pickedFolder.value = folder
  } else {
    folderMode.value = 'type'
    typedFolder.value = folder
  }
}

// as the dialog opens: the folders now, the default one chosen
const refresh = async () => {
  list.value = await fetchFolders().catch(() => null)
  useFolder(list.value?.progress ?? '')
}

defineExpose({ refresh })
</script>

<template>
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
</template>

<style module>

.field {
  composes: field from '../ui.module.css';
}

.option {
  composes: option from '../ui.module.css';
}

.select {
  composes: select from '../ui.module.css';
}

.select:disabled,
.field input[type='text']:disabled {
  opacity: 0.5;
}

</style>
