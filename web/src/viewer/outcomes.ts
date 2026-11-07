// what the converter does with a piece, as the preview colours it; null colour = drawn as is
export const outcomes: Record<string, { label: string; color: number | null }> = {
  custom: { label: 'Custom ice block', color: 0x3fa9f5 },
  twin: { label: 'Native ice block', color: 0x7fe0f0 },
  tops: { label: 'Ice on its tops', color: 0xe8f4ff },
  item: { label: 'Ice item', color: 0xb48cf0 },
  skin: { label: 'Ice skin', color: 0x5f6cff },
  ice: { label: 'Ice copy (item)', color: 0xb48cf0 },
  swapped: { label: 'Swapped (frozen)', color: 0xffffff },
  sunk: { label: 'Sunk into the ice', color: 0xc0c0c0 },
  removed: { label: 'Removed', color: 0x8a8a8a },
  kept: { label: 'Kept (scenery off)', color: null },
  unchanged: { label: 'Unchanged', color: null },
}

export function outcomeLabel(outcome: string) {
  return outcomes[outcome]?.label ?? outcome
}

// where the name says less than the option does
const optionLabels: Record<string, string> = {
  convertTrees: 'Convert trees on converted blocks',
}

// convertScenery -> Convert scenery
export function optionLabel(name: string) {
  if (optionLabels[name]) return optionLabels[name]
  const words = name.replace(/([A-Z])/g, ' $1').toLowerCase()
  return words[0].toUpperCase() + words.slice(1)
}
