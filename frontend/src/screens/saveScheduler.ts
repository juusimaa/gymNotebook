// Autosave for the session editor (specs/003-durable-logging D2). A logged
// set should be as durable as ink, so an in-progress page saves itself: about
// two seconds after the last edit, or at once when a set row becomes complete.
//
// This module decides *when* to save; the editor decides *what* a save is
// (the `save` callback reads the latest draft when it runs). Keeping the timing
// here, free of React, is what lets Vitest walk it with fake timers.
//
// The rules, as a small state machine:
//
//   idle ──edit──▶ waiting ──2 s──▶ saving ──ok──▶ idle
//                     ▲                 │
//                     └──edit (retry)── failed ◀──error
//
//   - One save in flight at a time. An edit during a save marks the draft
//     dirty, and the next save starts when this one ends, so saves never
//     overlap or arrive out of order.
//   - A failed save is retried after 5 s, 15 s, then every 60 s; a new edit
//     retries at once (FR-006).
//   - A save can answer "stopped" (the page changed on another device, or no
//     longer exists): nothing more is sent until the editor resumes it.

export type SaveOutcome = 'saved' | 'failed' | 'stopped'

export type SaveStatus =
  | { kind: 'idle' }
  | { kind: 'waiting' }
  // `slow` turns true once a save has taken over a second, so "Saving…" is
  // shown (and announced) only for a save the lifter could notice.
  | { kind: 'saving'; slow: boolean }
  | { kind: 'failed'; attempt: number }
  | { kind: 'stopped' }

export interface SaveSchedulerOptions {
  // Sends the editor's current draft. A thrown error counts as 'failed'.
  save: () => Promise<SaveOutcome>
  onStatus: (status: SaveStatus) => void
  debounceMs?: number
  slowMs?: number
  retryDelaysMs?: readonly number[]
}

export interface SaveScheduler {
  // Something changed. `immediate` skips the debounce: a set just became
  // complete, which is the moment worth keeping.
  edit(options?: { immediate?: boolean }): void
  // Saves anything pending now and waits for it. True when every edit so far
  // has reached the server; false on a failure or when stopped.
  flush(): Promise<boolean>
  // No more saves (leaving, tearing the page out). Resolves once a save in
  // flight has ended, so a following request can't overtake it.
  stop(): Promise<void>
  // Starts again after stop() or a stopped save: when the editor reloaded
  // the page, after an explicit save failed, or when it mounts again (React's
  // StrictMode mounts twice in development). An edit still pending is saved
  // after the usual debounce; the save itself finds out if there's nothing
  // to send.
  resume(): void
}

export const AUTOSAVE_DEBOUNCE_MS = 2_000
export const SLOW_SAVE_MS = 1_000
export const RETRY_DELAYS_MS = [5_000, 15_000, 60_000] as const

type Timer = ReturnType<typeof setTimeout>

export function createSaveScheduler({
  save,
  onStatus,
  debounceMs = AUTOSAVE_DEBOUNCE_MS,
  slowMs = SLOW_SAVE_MS,
  retryDelaysMs = RETRY_DELAYS_MS,
}: SaveSchedulerOptions): SaveScheduler {
  // Edits not yet included in a save that has started.
  let dirty = false
  // An edit during a save asked for no debounce.
  let immediateNext = false
  let failures = 0
  let stopped = false
  let inFlight: Promise<SaveOutcome> | null = null
  // The debounce or the retry wait; never both.
  let timer: Timer | null = null
  let slowTimer: Timer | null = null

  function clearTimer() {
    if (timer !== null) clearTimeout(timer)
    timer = null
  }

  function startAfter(ms: number) {
    clearTimer()
    timer = setTimeout(() => {
      timer = null
      start()
    }, ms)
  }

  function start() {
    clearTimer()
    if (stopped || inFlight !== null || !dirty) return
    dirty = false
    immediateNext = false
    onStatus({ kind: 'saving', slow: false })
    slowTimer = setTimeout(() => {
      slowTimer = null
      if (inFlight !== null) onStatus({ kind: 'saving', slow: true })
    }, slowMs)
    inFlight = run()
  }

  async function run(): Promise<SaveOutcome> {
    let outcome: SaveOutcome
    try {
      outcome = await save()
    } catch {
      outcome = 'failed'
    }
    if (slowTimer !== null) clearTimeout(slowTimer)
    slowTimer = null
    inFlight = null

    // stop() was called while this save ran: report nothing further.
    if (stopped) return outcome

    if (outcome === 'stopped') {
      stopped = true
      onStatus({ kind: 'stopped' })
    } else if (outcome === 'failed') {
      // What this save carried didn't arrive, so it is still to be sent.
      dirty = true
      failures += 1
      const delay =
        retryDelaysMs[Math.min(failures, retryDelaysMs.length) - 1] ?? 60_000
      onStatus({ kind: 'failed', attempt: failures })
      startAfter(delay)
    } else {
      failures = 0
      if (!dirty) {
        onStatus({ kind: 'idle' })
      } else if (immediateNext) {
        start()
      } else {
        onStatus({ kind: 'waiting' })
        startAfter(debounceMs)
      }
    }
    return outcome
  }

  return {
    edit({ immediate = false } = {}) {
      if (stopped) return
      dirty = true
      if (inFlight !== null) {
        // Goes in the next save, which starts when this one ends.
        immediateNext ||= immediate
        return
      }
      // A failed save waiting to retry goes at once on a new edit.
      if (immediate || failures > 0) {
        start()
      } else {
        onStatus({ kind: 'waiting' })
        startAfter(debounceMs)
      }
    },

    async flush() {
      for (;;) {
        if (stopped) return false
        if (inFlight !== null) {
          await inFlight
          continue
        }
        if (!dirty) return true
        start()
        // start() always sets inFlight here: not stopped, nothing in flight,
        // and dirty.
        const outcome = await (inFlight as Promise<SaveOutcome> | null)
        if (outcome !== 'saved') return false
      }
    },

    async stop() {
      stopped = true
      clearTimer()
      if (inFlight !== null) await inFlight
    },

    resume() {
      clearTimer()
      stopped = false
      failures = 0
      if (inFlight !== null) return
      if (dirty) {
        onStatus({ kind: 'waiting' })
        startAfter(debounceMs)
      } else {
        onStatus({ kind: 'idle' })
      }
    },
  }
}

// The incomplete-row notice (FR, Story 1 scenario 4): a row with a weight but
// no reps (or the reverse) isn't saved, and after it has been left like that
// for a minute the date line says so. A minute, so it doesn't nag between
// typing the weight and the reps (owner, 2026-10-07).
//
// `observe` takes the key of the first incomplete row, or null when there is
// none, on every change. The same key keeps its clock running; a different
// one starts it again. `onDue` gets the key once its minute is up, and null
// when the notice should go.
export const INCOMPLETE_NOTICE_MS = 60_000

export interface IncompleteRowWatch {
  observe(key: string | null): void
  dispose(): void
}

export function createIncompleteRowWatch(
  onDue: (key: string | null) => void,
  delayMs: number = INCOMPLETE_NOTICE_MS,
): IncompleteRowWatch {
  let current: string | null = null
  let shown = false
  let timer: Timer | null = null

  function clear() {
    if (timer !== null) clearTimeout(timer)
    timer = null
  }

  return {
    observe(key) {
      if (key === current) return
      clear()
      current = key
      if (shown) {
        shown = false
        onDue(null)
      }
      if (key !== null) {
        timer = setTimeout(() => {
          timer = null
          shown = true
          onDue(key)
        }, delayMs)
      }
    },
    dispose: clear,
  }
}
