<script setup lang="ts">
import FoldSection from './FoldSection.vue'
import OptionList from './OptionList.vue'
// presets parked until there are reasonable ones
// import { usePreviewStore } from '../stores/preview'
// import PresetPicker from './PresetPicker.vue'
// const preview = usePreviewStore()
</script>

<template>
  <div :class="$style.menu">
    <FoldSection id="map-options" title="Map options">
      <OptionList />
    </FoldSection>
    <!-- presets parked until there are reasonable ones
    <FoldSection id="presets" title="Presets">
      <PresetPicker v-if="preview.state" />
    </FoldSection>
    -->
  </div>
</template>

<style module>
.menu {
  display: grid;
  gap: 6px;
}
</style>
