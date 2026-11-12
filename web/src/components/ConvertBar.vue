<script setup lang="ts">
import { useConvertStore } from '../stores/convert'

defineEmits<{ show: [] }>()

const converting = useConvertStore()
</script>

<template>
  <div v-if="converting.job && !converting.inDialog" :class="[$style.bar, $style[converting.job.state]]">
    <button type="button" :class="$style.barMain" :title="`Show the conversion of ${converting.job.name}`" @click="$emit('show')">
      <span :class="$style.barText">
        <template v-if="converting.job.state === 'running'">{{ converting.job.cancelling ? 'Cancelling' : 'Converting' }} {{ converting.job.name }} · {{ converting.stepNow }}</template>
        <template v-else-if="converting.job.state === 'done'">Converted {{ converting.job.name }}</template>
        <template v-else-if="converting.job.state === 'cancelled'">Cancelled {{ converting.job.name }}</template>
        <template v-else>Conversion of {{ converting.job.name }} failed</template>
      </span>
      <span v-if="converting.job.state === 'running'" :class="$style.barPercent">{{ converting.percent }}%</span>
      <span :class="$style.barTrack" aria-hidden="true"><span :class="$style.barFill" :style="{ width: `${converting.percent}%` }" /></span>
    </button>
    <button v-if="converting.job.state !== 'running'" type="button" :class="$style.barClose" aria-label="Dismiss" @click="converting.dismiss()">×</button>
  </div>
</template>

<style module>
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
</style>
