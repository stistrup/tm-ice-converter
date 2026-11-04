<script setup lang="ts">
import { onMounted } from 'vue'
import InspectPanel from './components/InspectPanel.vue'
import MapList from './components/MapList.vue'
import MapViewport from './components/MapViewport.vue'
import { useMapStore } from './stores/map'

const store = useMapStore()

onMounted(store.loadMaps)
</script>

<template>
  <div :class="$style.layout">
    <MapList />
    <MapViewport />
    <InspectPanel />
  </div>
</template>

<style module>
.layout {
  display: grid;
  grid-template-columns: 280px minmax(0, 1fr) 320px;
  height: 100%;
}
</style>
