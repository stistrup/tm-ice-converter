<script setup lang="ts">
import { computed } from 'vue'
import { useHistoryStore } from '../stores/history'
import { usePreviewStore } from '../stores/preview'
import TargetList from './TargetList.vue'

const preview = usePreviewStore()
const history = useHistoryStore()

// determinate while pieces build, running without a number while the preview is worked out
const work = computed(() => {
  const b = preview.building
  if (b && b.total) return { text: `Building pieces ${b.done} of ${b.total}`, percent: Math.round((b.done / b.total) * 100) }
  if (preview.loading && !preview.showOriginal) return { text: 'Working out the preview', percent: null }
  return null
})
</script>

<template>
  <section :class="$style.bar">
    <div :class="$style.row">
      <label :class="$style.option">
        <input type="radio" name="shown" :checked="preview.showOriginal" @change="preview.showOriginal = true" />
        <span>Original</span>
      </label>
      <label :class="$style.option">
        <input type="radio" name="shown" :checked="!preview.showOriginal" @change="preview.showOriginal = false" />
        <span>Preview</span>
      </label>
      <span :class="$style.grow" />
      <template v-if="preview.dirty">
        <span :class="$style.muted">Not saved</span>
        <button type="button" :class="$style.textButton" @click="history.revert()">Revert</button>
        <button type="button" :class="$style.smallButton" @click="preview.save()">Save</button>
      </template>
    </div>
    <TargetList />
    <div v-if="work" :class="$style.progress" role="progressbar" :aria-valuenow="work.percent ?? undefined" aria-valuemin="0" aria-valuemax="100">
      <div :class="[$style.fill, { [$style.unknown]: work.percent === null }]" :style="{ width: `${work.percent ?? 100}%` }" />
      <span :class="$style.label">{{ work.text }}</span>
    </div>
    <p v-if="preview.error" :class="$style.error">{{ preview.error }}</p>
    <div v-if="preview.result?.problems.length || preview.result?.stalePicks.length || preview.state?.unreadable.length" :class="$style.problems">
      <p v-for="problem in preview.result?.problems ?? []" :key="problem">Left out: {{ problem }}</p>
      <p v-if="preview.result?.stalePicks.length" :title="preview.result.stalePicks.join('\n')">
        {{ preview.result.stalePicks.length }} conversion target(s) for pieces not on this map
      </p>
      <p v-for="file in preview.state?.unreadable ?? []" :key="file">Unreadable: {{ file }}</p>
    </div>
  </section>
</template>

<style module>
.bar {
  position: sticky;
  top: 0;
  z-index: 1;
  display: grid;
  gap: 4px;
  padding: 6px 8px;
  background: var(--panel);
  border-bottom: 1px solid var(--line);
}

.row {
  display: flex;
  gap: 6px;
  align-items: center;
  min-height: 20px;
}

.error {
  composes: error from '../ui.module.css';
  margin: 0;
}

.problems {
  display: grid;
  gap: 2px;
  max-height: 60px;
  padding: 2px 6px;
  overflow-y: auto;
  color: var(--accent);
  border: 1px solid var(--line);
  border-radius: 2px;
}

.problems p {
  margin: 0;
  overflow-wrap: anywhere;
}

.progress {
  position: relative;
  height: 20px;
  overflow: hidden;
  background: var(--field);
  border: 1px solid var(--accent);
  border-radius: 2px;
}

.fill {
  position: absolute;
  inset: 0 auto 0 0;
  background: color-mix(in srgb, var(--accent) 45%, transparent);
  transition: width 0.3s ease-out;
}

/* no number yet: a faint full bar, so it doesn't read as done */
.unknown {
  background: color-mix(in srgb, var(--accent) 18%, transparent);
}

/* a sheen moving across: working, even while one piece takes long */
.fill::after {
  position: absolute;
  inset: 0;
  content: '';
  background: linear-gradient(90deg, transparent, rgb(255 255 255 / 25%), transparent);
  background-size: 120px 100%;
  background-repeat: no-repeat;
  animation: sheen 1.2s linear infinite;
}

@keyframes sheen {
  from {
    background-position: -120px 0;
  }

  to {
    background-position: calc(100% + 120px) 0;
  }
}

.label {
  position: relative;
  display: block;
  padding: 0 6px;
  line-height: 18px;
  color: var(--strong);
}

.muted {
  composes: muted from '../ui.module.css';
}

.option {
  composes: option from '../ui.module.css';
}

.grow {
  composes: grow from '../ui.module.css';
}

.smallButton {
  composes: smallButton from '../ui.module.css';
}

.textButton {
  composes: textButton from '../ui.module.css';
}
</style>
