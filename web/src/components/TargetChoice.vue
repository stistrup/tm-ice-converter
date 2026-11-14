<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { readSession, writeSession } from '../storage'
import { useHistoryStore } from '../stores/history'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'
import DropDown from './DropDown.vue'
import FloatingPanel from './FloatingPanel.vue'
import { useHoverFocus } from '../hoverFocus'
import FocusEye from './FocusEye.vue'
import { displayName } from '../viewer/names'

const store = useMapStore()
const preview = usePreviewStore()
const history = useHistoryStore()

// what the pieces are and can become comes with a preview: work one out as soon as something is selected
watch(
  () => store.selection.length > 0 && !preview.result && !preview.loading,
  (needed) => {
    if (needed) void preview.run()
  },
  { immediate: true },
)

const combo = (material: string, way: string | null) => `${material}|${way ?? ''}`
const withOption = (base: string, option: string) => `${base}#${option}`

// each selected piece: its key, its target now, and per material|way(#option) the pick that does it there
const pieces = computed(() => {
  const result = preview.result
  if (!result) return []
  return store.selection.flatMap((index) => {
    const d = preview.decisions?.[index]
    if (!d?.kind) return []
    const can = new Map<string, string>()
    for (const c of result.choices[d.kind] ?? [])
    {
      const at = c.option ? withOption(combo(c.material, c.way), c.option) : combo(c.material, c.way)
      if (!d.cannot?.includes(c.value) && !can.has(at)) can.set(at, c.value)
    }
    return [{ index, key: d.key, now: preview.picks[d.key] ?? '', can }]
  })
})

const words = computed(() => preview.result?.words ?? null)
const material = ref('')
const way = ref('')
const ticked = ref(new Set<string>())

const count = (test: (combo: string) => boolean) => pieces.value.filter((p) => [...p.can.keys()].some(test)).length

const counted = (label: string, n: number) => (n < pieces.value.length ? `${label} (${n}/${pieces.value.length})` : label)

const materials = computed(() =>
  Object.entries(words.value?.materials ?? {})
    // "original" is no conversion: a reset to original is parked
    .filter(([value]) => value !== 'original')
    .map(([value, w]) => ({ value, n: count((c) => c.startsWith(`${value}|`)), label: w.label }))
    .filter((m) => m.n > 0),
)

const ways = computed(() =>
  Object.entries(words.value?.ways ?? {})
    .map(([value, w]) => {
      const base = combo(material.value, value)
      return { value, n: count((c) => c === base || c.startsWith(`${base}#`)), label: w.label, title: w.description }
    })
    .filter((w) => w.n > 0),
)

const wanted = computed(() => combo(material.value, ways.value.length ? way.value : null))

// ticks the chosen material and way have on some selected piece
const options = computed(() =>
  Object.entries(words.value?.options ?? {})
    .map(([value, o]) => ({ value, n: count((c) => c === withOption(wanted.value, value)), label: o.label, title: o.description }))
    .filter((o) => o.n > 0),
)

const tick = (option: string, on: boolean) => {
  const next = new Set(ticked.value)
  if (on) next.add(option)
  else next.delete(option)
  ticked.value = next
}

// the pieces the choice reaches, with the pick each takes
const reached = computed(() => {
  return pieces.value.flatMap((p) => {
    const option = options.value.find((o) => ticked.value.has(o.value) && p.can.has(withOption(wanted.value, o.value)))
    const pick = option ? p.can.get(withOption(wanted.value, option.value)) : p.can.get(wanted.value)
    return pick === undefined ? [] : [{ ...p, pick }]
  })
})
// some, not none and not all: the rest stay selected for another choice
const partial = computed(() => reached.value.length > 0 && reached.value.length < pieces.value.length)
const changes = computed(() => reached.value.some((p) => p.now !== p.pick))

// the choice survives a reload in this session; it starts over when the selection empties, or one piece outside it gets selected
interface Kept {
  material: string
  way: string
  ticked: string[]
  selection: number[]
}
const KEY = 'convert-choice'
let before: number[] = []
{
  const kept = readSession<Kept | null>(KEY, null)
  if (kept) {
    before = kept.selection
    material.value = kept.material
    way.value = kept.way
    ticked.value = new Set(kept.ticked)
  }
}

// from the selection's own target when they share one
const startOver = () => {
  const now = new Set(pieces.value.map((p) => p.now))
  const pick = now.size === 1 ? [...now][0] : null
  const choice = pick === null ? null : Object.values(preview.result?.choices ?? {}).flat().find((c) => c.value === pick)
  material.value = materials.value.find((m) => m.value === choice?.material)?.value ?? materials.value[0]?.value ?? ''
  way.value = choice?.way ?? ''
  ticked.value = new Set(choice?.option ? [choice.option] : [])
}

watch(
  () => [store.selection, !!preview.result],
  () => {
    const selection = store.selection
    if (!selection.length) {
      before = []
      return
    }
    if (!preview.result) return
    const fresh = !before.length || (selection.length === 1 && !before.includes(selection[0]))
    if (fresh || !materials.value.some((m) => m.value === material.value)) startOver()
    before = selection
  },
  { immediate: true },
)

watch(
  () => [material.value, way.value, ticked.value, store.selection],
  () => {
    writeSession(KEY, { material: material.value, way: way.value, ticked: [...ticked.value], selection: before } satisfies Kept)
  },
)

// a way the material doesn't have: its first
watch(ways, (now) => {
  if (now.length && !now.some((w) => w.value === way.value)) way.value = now[0].value
})

// the selection as a list, opened by hovering the count
const listOpen = ref(false)
const heading = ref<HTMLElement>()
let closing = 0
let opening = 0
// from the heading after a moment; onto the open list at once
const showList = (delay = 0) => {
  clearTimeout(closing)
  if (listOpen.value) return
  clearTimeout(opening)
  opening = window.setTimeout(() => (listOpen.value = true), delay)
}
const hideList = () => {
  clearTimeout(opening)
  clearTimeout(closing)
  closing = window.setTimeout(() => (listOpen.value = false), 200)
}
const { enter, leave, keep } = useHoverFocus()
watch(listOpen, (now) => now || leave())
const listed = computed(() =>
  store.selection.map((index) => {
    const info = store.infos[index]
    return { index, name: info ? displayName(info) : `#${index}` }
  }),
)

// every selected piece and what Apply makes it: the ones the choice reaches first
const pickLabels = computed(() => new Map(Object.values(preview.result?.choices ?? {}).flat().map((c) => [c.value, c.label])))
const reach = computed(() => {
  const hit = new Map(reached.value.map((p) => [p.index, p.pick]))
  const now = new Map(pieces.value.map((p) => [p.index, p.now]))
  const rows = listed.value.map((piece) => {
    const pick = now.get(piece.index) ?? ''
    const label = (value: string) => (value ? (pickLabels.value.get(value) ?? value) : 'No target')
    const next = hit.get(piece.index)
    return { ...piece, applies: next !== undefined, next: next === undefined ? null : label(next), same: next === pick }
  })
  return [...rows.filter((r) => r.applies), ...rows.filter((r) => !r.applies)]
})

// selected pieces on the target list: Revert takes their targets away
const targeted = computed(() => pieces.value.filter((p) => p.now !== ''))
const revert = () => {
  const keys = targeted.value.map((p) => p.key)
  history.change(() => preview.pick(keys, ''))
}

const apply = () => {
  const done = reached.value
  const byPick = new Map<string, string[]>()
  for (const p of done) byPick.set(p.pick, [...(byPick.get(p.pick) ?? []), p.key])
  const out = new Set(done.map((p) => p.index))
  const deselect = partial.value
  history.change(() => {
    for (const [pick, keys] of byPick) preview.pick(keys, pick)
    if (deselect) store.selection = store.selection.filter((i) => !out.has(i))
  })
}
</script>

<template>
  <section :class="$style.choice">
    <template v-if="!store.selection.length" />
    <p v-else-if="!preview.result" :class="$style.muted">Calculating preview…</p>
    <template v-else-if="pieces.length">
      <div :class="$style.headRow">
        <h3 ref="heading" :class="$style.heading" @mouseenter="showList(500)" @mouseleave="hideList">{{ store.selection.length }} selected</h3>
        <button type="button" :class="$style.deselect" @click="history.change(() => (store.selection = []))">Deselect all</button>
      </div>
      <FloatingPanel v-model="listOpen" :anchor="heading" :title="`Selected: ${store.selection.length}`"
        fit @enter="showList()" @leave="hideList">
        <li v-for="piece in listed" :key="piece.index" @click="keep(piece.index, false)">
          <span :class="$style.grow">{{ piece.name }}</span>
          <FocusEye @enter="enter(piece.index)" @leave="leave()" @keep="keep(piece.index, false)" />
        </li>
      </FloatingPanel>
      <div :class="$style.row">
        <span :class="$style.word">Convert to</span>
        <DropDown v-model="material" :options="materials.map((m) => ({ value: m.value, label: counted(m.label, m.n) }))" aria-label="Material" />
      </div>
      <div v-if="ways.length" :class="$style.row">
        <span :class="$style.word">by</span>
        <DropDown v-model="way" :options="ways.map((w) => ({ value: w.value, label: counted(w.label, w.n), title: w.title }))" aria-label="Way" />
      </div>
      <label v-for="o in options" :key="o.value" :class="$style.option" :title="o.title">
        <input type="checkbox" :checked="ticked.has(o.value)" @change="tick(o.value, ($event.target as HTMLInputElement).checked)" />
        <span>{{ counted(o.label, o.n) }}</span>
      </label>
      <ul :class="$style.reach" aria-label="What the choice reaches">
        <li
          v-for="piece in reach"
          :key="piece.index"
          :class="{ [$style.out]: !piece.applies }"
          :title="piece.applies ? undefined : 'Not for this one'"
          @click="keep(piece.index, false)"
        >
          <span :class="$style.name">{{ piece.name }}</span>
          <template v-if="piece.next">
            <span :class="$style.arrow">→</span>
            <span :class="[$style.becomes, $style.next]" :title="piece.same ? `Already ${piece.next}` : 'After Apply'">{{ piece.same ? 'Unchanged' : piece.next }}</span>
          </template>
          <FocusEye :class="$style.eyeEnd" @enter="enter(piece.index)" @leave="leave()" @keep="keep(piece.index, false)" />
        </li>
      </ul>
      <div :class="$style.actions">
        <button v-if="targeted.length" type="button" :class="[$style.button, $style.revert]" title="Take their targets away" @click="revert">
          {{ targeted.length < pieces.length ? `Revert ${targeted.length}` : 'Revert' }}
        </button>
        <button type="button" :class="$style.button" :disabled="!reached.length || !changes" @click="apply">
          {{ partial ? `Apply to ${reached.length} and deselect` : 'Apply' }}
        </button>
      </div>
    </template>
    <p v-else :class="$style.muted">Nothing here to convert</p>
  </section>
</template>

<style module>
.choice {
  display: grid;
  gap: 4px;
}

.grow {
  composes: grow from '../ui.module.css';
}

.headRow {
  display: flex;
  gap: 8px;
  align-items: center;
  justify-content: space-between;
}

.deselect {
  padding: 0 4px;
  color: var(--text);
  cursor: default;
  background: none;
  border: 0;
}

.deselect:hover {
  color: var(--strong);
}

.heading {
  justify-self: start;
  margin: 4px 0 2px;
  font-size: 12px;
  font-weight: 600;
  color: var(--strong);
}

.row {
  display: flex;
  gap: 6px;
  align-items: center;
}

.word {
  flex: none;
  width: 64px;
  color: var(--muted);
}

.select {
  composes: select from '../ui.module.css';
  flex: 1;
  min-width: 0;
}

.option {
  composes: option from '../ui.module.css';
  padding-left: 70px;
}

.reach {
  max-height: 140px;
  margin: 2px 0 0;
  padding: 2px 0;
  overflow-y: auto;
  font-size: 11px;
  list-style: none;
  background: var(--field);
  border: 1px solid var(--line);
  border-radius: 2px;
}

.reach li {
  display: flex;
  gap: 6px;
  align-items: center;
  padding: 1px 6px;
  white-space: nowrap;
  cursor: default;
}

/* pushed to the row's end, kept to the small rows' height */
.eyeEnd {
  height: 14px;
  margin-left: auto;
}

.reach li:hover {
  background: var(--hover);
}

.name {
  flex: 0 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}

.out {
  color: var(--muted);
  opacity: 0.6;
}

.arrow {
  flex: none;
  color: var(--muted);
}

.becomes {
  flex: none;
  color: var(--muted);
}

.next {
  color: var(--text);
}

.actions {
  display: flex;
  justify-content: flex-end;
  margin-top: 2px;
}

.button {
  composes: button from '../ui.module.css';
}

.revert {
  margin-right: auto;
}

.muted {
  composes: muted from '../ui.module.css';
  margin: 0;
  font-size: 11px;
}
</style>
