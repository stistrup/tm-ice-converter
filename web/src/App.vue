<script setup lang="ts">
import { onMounted } from 'vue'
import MapList from './components/MapList.vue'
import MapViewport from './components/MapViewport.vue'
import StatusBar from './components/StatusBar.vue'
import { startLiveLink } from './live'
import { useMapStore } from './stores/map'

const store = useMapStore()

onMounted(() => {
  void store.loadMaps()
  startLiveLink()
})
</script>

<template>
  <div :class="$style.layout">
    <div :class="$style.side">
      <MapList v-model:shown="store.mapsShown" :class="[$style.box, { [$style.open]: store.mapsShown }]" />
    </div>
    <MapViewport />
    <StatusBar :class="$style.status" />
  </div>
</template>

<style module>
.layout {
  position: relative;
  display: grid;
  grid-template-rows: minmax(0, 1fr) 22px;
  grid-template-columns: minmax(0, 1fr);
  gap: 1px;
  height: 100%;
  background: var(--line);
}

/* maps over properties, floating over the view so it never resizes */
.side {
  position: absolute;
  top: 0;
  bottom: 23px;
  left: 0;
  z-index: 1;
  display: flex;
  flex-direction: column;
  width: 261px;
  pointer-events: none;
}

.box {
  flex: none;
  pointer-events: auto;
  border-right: 1px solid var(--line);
}

.open {
  flex: 1 1 0;
}

.status {
  grid-column: 1 / -1;
}
</style>
