<script setup lang="ts">
import { computed } from 'vue'
import type { SetupStep } from '../api'
import { useSetupStore } from '../stores/setup'

const emit = defineEmits<{ done: [] }>()
const setup = useSetupStore()
const steps = computed(() => setup.status?.steps ?? [])
const editable = new Set(['game', 'trackmania', 'openplanet', 'blender'])
const marks = { ok: '✓', todo: '•', wait: '–', running: '…' }

function others(step: SetupStep) {
  return (step.found ?? []).filter((f) => f !== step.path)
}

function save(step: SetupStep, event: Event) {
  const value = (event.target as HTMLInputElement).value.trim()
  if (value && value !== step.path) void setup.setPath(step.id, value)
}

function act(step: SetupStep) {
  void setup.act(step.id === 'extract' && step.state === 'running' ? 'extract-stop' : (step.actionId ?? step.id))
}
</script>

<template>
  <div :class="$style.page">
    <div :class="$style.panel">
      <header :class="$style.header">Ice Converter setup</header>
      <p v-if="setup.lost" :class="$style.error">The converter isn't responding; start it again</p>
      <p v-else-if="setup.error" :class="$style.error">{{ setup.error }}</p>
      <ol :class="$style.steps">
        <li v-for="step in steps" :key="step.id" :class="[$style.step, $style[step.state]]">
          <span :class="$style.mark">{{ marks[step.state] }}</span>
          <div :class="$style.main">
            <div :class="$style.title">{{ step.title }}</div>
            <div :class="$style.text">{{ step.text }}</div>
            <div v-if="step.note" :class="$style.note">{{ step.note }}</div>
            <input
              v-if="editable.has(step.id) && step.state !== 'wait'"
              :class="$style.path"
              :value="step.path ?? ''"
              :placeholder="step.id === 'blender' ? 'Path to blender' : 'Folder path'"
              spellcheck="false"
              @change="save(step, $event)"
              @keydown.enter="save(step, $event)"
            />
            <button v-for="f in others(step)" :key="f" :class="$style.found" type="button" @click="setup.setPath(step.id, f)">
              Use {{ f }}
            </button>
            <div v-if="step.state === 'running'" :class="$style.track">
              <span :class="[$style.fill, { [$style.endless]: step.progress == null }]" :style="{ width: `${(step.progress ?? 0.3) * 100}%` }" />
            </div>
          </div>
          <div :class="$style.actions">
            <a v-if="step.link" :class="$style.button" :href="step.link" target="_blank" rel="noopener">Download</a>
            <button
              v-if="step.action"
              :class="[$style.button, { [$style.primary]: step.state === 'todo' }]"
              type="button"
              :disabled="!step.actionEnabled"
              @click="act(step)"
            >
              {{ step.action }}
            </button>
          </div>
        </li>
      </ol>
      <footer :class="$style.footer">
        <button :class="[$style.button, $style.primary]" type="button" :disabled="!setup.status?.ready" @click="emit('done')">Open editor</button>
      </footer>
    </div>
  </div>
</template>

<style module>
.page {
  display: grid;
  place-items: start center;
  height: 100%;
  padding: 48px 16px;
  overflow: auto;
  background: var(--viewport);
}

.panel {
  width: min(560px, 100%);
  background: var(--panel);
  border: 1px solid var(--line);
  border-radius: 3px;
  box-shadow: 0 8px 24px rgb(0 0 0 / 40%);
}

.header {
  display: flex;
  align-items: center;
  height: 28px;
  padding: 0 10px;
  font-weight: 600;
  color: var(--strong);
  background: var(--header);
}

.steps {
  margin: 0;
  padding: 4px 0;
  list-style: none;
}

.step {
  display: flex;
  gap: 8px;
  align-items: flex-start;
  padding: 8px 10px;
  border-bottom: 1px solid var(--line);
}

.step:last-child {
  border-bottom: 0;
}

.mark {
  flex: none;
  width: 14px;
  font-weight: 700;
  text-align: center;
}

.ok .mark {
  color: #6cbf6c;
}

.todo .mark,
.running .mark {
  color: var(--accent);
}

.wait {
  color: var(--muted);
}

.main {
  display: grid;
  flex: 1;
  gap: 4px;
  min-width: 0;
}

.title {
  color: var(--strong);
}

.wait .title {
  color: var(--muted);
}

.text {
  color: var(--muted);
}

.note {
  color: var(--warn);
}

.path {
  font-family: var(--mono);
  font-size: 11px;
}

.found {
  justify-self: start;
  max-width: 100%;
  padding: 0;
  overflow: hidden;
  font-family: var(--mono);
  font-size: 11px;
  color: var(--accent);
  text-align: left;
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: default;
  background: none;
  border: 0;
}

.found:hover {
  text-decoration: underline;
}

.track {
  height: 4px;
  overflow: hidden;
  background: var(--field);
}

.fill {
  display: block;
  height: 100%;
  background: var(--accent);
  transition: width 0.5s;
}

.endless {
  animation: slide 1.4s ease-in-out infinite;
}

@keyframes slide {
  from {
    transform: translateX(-100%);
  }

  to {
    transform: translateX(340%);
  }
}

.actions {
  display: flex;
  flex: none;
  gap: 6px;
}

.button {
  display: inline-flex;
  align-items: center;
  height: 22px;
  padding: 0 12px;
  color: var(--text);
  text-decoration: none;
  cursor: default;
  background: #3a3a3a;
  border: 1px solid var(--line);
  border-radius: 2px;
}

.button:hover:not(:disabled) {
  background: #454545;
}

.primary,
.primary:hover {
  font-weight: 600;
  color: #1a1a1a;
  background: var(--accent);
}

.button:disabled {
  font-weight: normal;
  color: var(--muted);
  background: #333;
}

.error {
  margin: 0;
  padding: 6px 10px;
  color: var(--error);
  background: rgb(224 119 107 / 10%);
  border-bottom: 1px solid var(--line);
}

.footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  padding: 8px 10px;
  background: var(--header);
}

</style>
