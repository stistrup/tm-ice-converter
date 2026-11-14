<script setup lang="ts">
import { computed } from 'vue'
import { useConvertStore } from '../stores/convert'

defineEmits<{ close: []; background: [] }>()

const converting = useConvertStore()
const job = computed(() => converting.job!)
const running = computed(() => job.value.state === 'running')
const heading = computed(() => {
  const j = job.value
  if (j.state === 'running') return j.cancelling ? 'Cancelling' : 'Converting'
  return { done: 'Converted', cancelled: 'Cancelled', failed: 'Conversion failed:' }[j.state]
})

const stepState = (index: number): 'done' | 'now' | 'todo' => {
  const step = job.value.progress.step
  if (job.value.state === 'done' || index < step) return 'done'
  return index === step && job.value.state === 'running' ? 'now' : 'todo'
}
</script>

<template>
  <div :class="$style.form">
    <header :class="$style.header">
      <span id="convert-title">
        {{ heading }} {{ job.name }}
      </span>
    </header>
    <div :class="$style.body">
      <ol :class="$style.steps">
        <li v-for="(name, i) in job.progress.stepNames" :key="name" :class="$style[stepState(i)]">
          <span :class="$style.mark" aria-hidden="true">{{ stepState(i) === 'done' ? '✓' : stepState(i) === 'now' ? '›' : '' }}</span>
          <span :class="$style.grow">{{ name }}</span>
          <span v-if="stepState(i) === 'now' && job.progress.total" :class="$style.count">{{ job.progress.done }}/{{ job.progress.total }}</span>
          <span v-if="stepState(i) === 'now' && job.progress.detail" :class="$style.muted">{{ job.progress.detail }}</span>
        </li>
      </ol>
      <p v-if="job.progress.step < 0 && job.progress.detail" :class="$style.muted">{{ job.progress.detail }}…</p>
      <div :class="$style.overall">
        <span :class="$style.track" aria-hidden="true"><span :class="$style.fill" :style="{ width: `${converting.percent}%` }" /></span>
        <span :class="$style.count">{{ converting.percent }}%</span>
      </div>
      <ul v-if="job.count > 1 && (job.results.length || job.state === 'cancelled')" :class="$style.results" role="status">
        <li v-for="result in job.results" :key="result.map">
          <span v-if="result.output" :class="$style.ok">✓ {{ result.output }}</span>
          <span v-else :class="$style.error">✗ {{ result.map }}: {{ result.error }}</span>
        </li>
        <li v-if="job.state === 'cancelled'" :class="$style.muted">Cancelled: {{ job.results.filter((r) => r.output).length }} of {{ job.count }} maps saved.</li>
      </ul>
      <div v-if="job.outcome" :class="$style.result" role="status">
        <p :class="$style.ok">Saved: {{ job.outcome.output.startsWith('/') ? '' : 'Maps/' }}{{ job.outcome.output }}</p>
        <p v-for="problem in job.outcome.problems" :key="problem" :class="$style.warn">Left out: {{ problem }}</p>
        <p v-if="job.outcome.failures.length" :class="$style.warn" :title="job.outcome.failures.join('\n')">
          {{ job.outcome.failures.length }} piece(s) failed to build, see {{ job.outcome.report }}
        </p>
        <p v-if="job.outcome.missingFiles" :class="$style.warn">
          {{ job.outcome.missingFiles }} game files aren't extracted: in Openplanet: Plugins › Extract game files, then convert again
        </p>
      </div>
      <p v-if="job.state === 'done' && job.restartItems" :class="$style.restart" role="status">
        {{ job.restartItems }} item{{ job.restartItems === 1 ? '' : 's' }} built while the game ran: restart likely needed
      </p>
      <p v-if="job.error" :class="$style.error" role="alert">{{ job.error }}</p>
    </div>
    <footer :class="$style.actions">
      <template v-if="running">
        <button type="button" :class="$style.button" :disabled="job.cancelling" @click="converting.cancel()">
          {{ job.cancelling ? 'Cancelling…' : 'Cancel' }}
        </button>
        <button type="button" :class="$style.button" @click="$emit('background')">Continue in background</button>
      </template>
      <button v-else type="button" :class="$style.button" @click="$emit('close')">Close</button>
    </footer>
  </div>
</template>

<style module>
.steps {
  display: grid;
  gap: 2px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.steps li {
  display: flex;
  gap: 6px;
  align-items: center;
  min-height: 20px;
}

.mark {
  flex: none;
  width: 12px;
  text-align: center;
}

.steps .done {
  color: var(--text);
}

.steps .done .mark {
  color: #6cbf6c;
}

.steps .now {
  color: var(--strong);
  font-weight: 600;
}

.steps .now .mark {
  color: var(--accent);
}

.steps .todo {
  color: var(--muted);
}

.grow {
  flex: 1;
  min-width: 0;
}

.count {
  flex: none;
  color: var(--muted);
  font-weight: 400;
}

.overall {
  display: flex;
  gap: 8px;
  align-items: center;
}

.track {
  flex: 1;
  height: 8px;
  overflow: hidden;
  background: var(--field);
  border: 1px solid var(--line);
  border-radius: 4px;
}

.fill {
  display: block;
  height: 100%;
  background: var(--accent);
  transition: width 0.3s;
}

.form {
  margin: 0;
}

.header {
  display: flex;
  align-items: center;
  height: 24px;
  padding: 0 8px;
  color: var(--strong);
  background: var(--header);
}

.body {
  display: grid;
  gap: 10px;
  padding: 10px 8px;
}

.error {
  composes: error from '../ui.module.css';
}

.result {
  display: grid;
  gap: 3px;
}

.result p {
  margin: 0;
  overflow-wrap: anywhere;
}

.ok {
  color: var(--strong);
}

.results,
.batch {
  display: grid;
  gap: 2px;
  margin: 0;
  padding: 0;
  list-style: none;
  overflow-wrap: anywhere;
}

.restart {
  margin: 0;
  font-weight: 600;
  color: var(--accent);
}

.warn {
  margin: 0;
  color: var(--warn);
  overflow-wrap: anywhere;
}

.muted {
  composes: muted from '../ui.module.css';
}

.actions {
  display: flex;
  gap: 6px;
  justify-content: flex-end;
  padding: 0 8px 8px;
}

.button {
  composes: button from '../ui.module.css';
}

</style>
