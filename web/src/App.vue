<script setup lang="ts">
import { onMounted } from 'vue'
import MapList from './components/MapList.vue'
import MapViewport from './components/MapViewport.vue'
import StatusBar from './components/StatusBar.vue'
import { useMapStore } from './stores/map'

const store = useMapStore()

onMounted(store.loadMaps)
</script>

<template>
  <div :class="$style.layout">
    <MapList />
    <MapViewport />
    <StatusBar :class="$style.status" />
  </div>
</template>

<style module>
.layout {
  display: grid;
  grid-template-rows: minmax(0, 1fr) 22px;
  grid-template-columns: 260px minmax(0, 1fr);
  gap: 1px;
  height: 100%;
  background: var(--line);
}

.status {
  grid-column: 1 / -1;
}
</style>
