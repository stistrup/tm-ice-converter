<script setup lang="ts">
import { usePreviewStore } from '../stores/preview'
import FoldSection from './FoldSection.vue'
import OptionList from './OptionList.vue'
import PresetPicker from './PresetPicker.vue'

const preview = usePreviewStore()
</script>

<template>
  <div :class="$style.menu">
    <FoldSection id="map-options" title="Map options">
      <OptionList />
    </FoldSection>
    <FoldSection id="presets" title="Presets">
      <PresetPicker v-if="preview.state" />
    </FoldSection>
  </div>
</template>

<style module>
.menu {
  display: grid;
  gap: 6px;
}
</style>
