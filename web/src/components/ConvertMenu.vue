<script setup lang="ts">
import { usePreviewStore } from '../stores/preview'
import FoldSection from './FoldSection.vue'
import GroupConversion from './GroupConversion.vue'
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
      <template v-if="preview.state">
        <PresetPicker />
        <GroupConversion />
      </template>
    </FoldSection>
  </div>
</template>

<style module>
.menu {
  display: grid;
  gap: 6px;
}
</style>
