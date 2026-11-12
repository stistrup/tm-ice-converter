import { ref, watch, type Ref } from 'vue'

// browser storage, for per-viewer conveniences only: it can be empty or blocked, so reads fall back and writes may not stick

export const readJson = <T>(key: string, fallback: T): T => {
  try {
    const text = localStorage.getItem(key)
    return text === null ? fallback : (JSON.parse(text) as T)
  } catch {
    return fallback
  }
}

export const writeJson = (key: string, value: unknown) => {
  try {
    localStorage.setItem(key, JSON.stringify(value))
  } catch {
    // only a convenience
  }
}

export const readText = (key: string): string | null => {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}

// null forgets it
export const writeText = (key: string, value: string | null) => {
  try {
    if (value === null) localStorage.removeItem(key)
    else localStorage.setItem(key, value)
  } catch {
    // only a convenience
  }
}

// a ref kept in storage as JSON
export const remembered = <T>(key: string, fallback: T, deep = false): Ref<T> => {
  const value = ref(readJson(key, fallback)) as Ref<T>
  watch(value, (now) => writeJson(key, now), { deep })
  return value
}
