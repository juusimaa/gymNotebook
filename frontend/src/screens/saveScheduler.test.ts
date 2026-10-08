import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  createIncompleteRowWatch,
  createSaveScheduler,
  type SaveOutcome,
  type SaveStatus,
} from './saveScheduler'

// A save the test settles by hand, so it can check what happens while one is
// in flight.
function controllableSave() {
  const pending: ((outcome: SaveOutcome) => void)[] = []
  const save = vi.fn(
    () =>
      new Promise<SaveOutcome>((resolve) => {
        pending.push(resolve)
      }),
  )
  return {
    save,
    // Settles the oldest save still pending and lets its follow-ups run.
    settle: async (outcome: SaveOutcome) => {
      pending.shift()?.(outcome)
      await vi.advanceTimersByTimeAsync(0)
    },
  }
}

function setup() {
  const control = controllableSave()
  const statuses: SaveStatus[] = []
  const scheduler = createSaveScheduler({
    save: control.save,
    onStatus: (status) => statuses.push(status),
  })
  return { ...control, statuses, scheduler, last: () => statuses.at(-1) }
}

describe('createSaveScheduler', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('edit_Debounced_SavesTwoSecondsAfterTheLastEdit', async () => {
    // Arrange
    const { save, scheduler, last } = setup()

    // Act
    scheduler.edit()
    await vi.advanceTimersByTimeAsync(1_500)
    scheduler.edit()
    await vi.advanceTimersByTimeAsync(1_999)

    // Assert
    expect(save).not.toHaveBeenCalled()
    expect(last()).toEqual({ kind: 'waiting' })
    await vi.advanceTimersByTimeAsync(1)
    expect(save).toHaveBeenCalledTimes(1)
    expect(last()).toEqual({ kind: 'saving', slow: false })
  })

  it('edit_Immediate_SavesWithoutWaiting', () => {
    // Arrange
    const { save, scheduler } = setup()

    // Act
    scheduler.edit({ immediate: true })

    // Assert
    expect(save).toHaveBeenCalledTimes(1)
  })

  it('edit_DuringSave_WaitsAndGoesInTheNextSave', async () => {
    // Arrange
    const { save, settle, scheduler, last } = setup()
    scheduler.edit({ immediate: true })

    // Act
    scheduler.edit({ immediate: true })
    scheduler.edit()

    // Assert: still one save until the first answers, then exactly one more.
    expect(save).toHaveBeenCalledTimes(1)
    await settle('saved')
    expect(save).toHaveBeenCalledTimes(2)
    await settle('saved')
    expect(save).toHaveBeenCalledTimes(2)
    expect(last()).toEqual({ kind: 'idle' })
  })

  it('edit_DuringSaveNotImmediate_DebouncesTheNextSave', async () => {
    // Arrange
    const { save, settle, scheduler, last } = setup()
    scheduler.edit({ immediate: true })
    scheduler.edit()

    // Act
    await settle('saved')

    // Assert
    expect(last()).toEqual({ kind: 'waiting' })
    expect(save).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(2_000)
    expect(save).toHaveBeenCalledTimes(2)
  })

  it('save_TakesOverASecond_ReportsSlow', async () => {
    // Arrange
    const { settle, scheduler, last } = setup()

    // Act
    scheduler.edit({ immediate: true })
    await vi.advanceTimersByTimeAsync(999)
    const beforeSecond = last()
    await vi.advanceTimersByTimeAsync(1)

    // Assert
    expect(beforeSecond).toEqual({ kind: 'saving', slow: false })
    expect(last()).toEqual({ kind: 'saving', slow: true })
    await settle('saved')
    expect(last()).toEqual({ kind: 'idle' })
  })

  it('save_Fails_RetriesAfter5Then15ThenEvery60Seconds', async () => {
    // Arrange
    const { save, settle, scheduler, last } = setup()
    scheduler.edit({ immediate: true })

    // Act / Assert: each wait is the next delay in the back-off.
    await settle('failed')
    expect(last()).toEqual({ kind: 'failed', attempt: 1 })
    await vi.advanceTimersByTimeAsync(4_999)
    expect(save).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(save).toHaveBeenCalledTimes(2)

    await settle('failed')
    await vi.advanceTimersByTimeAsync(14_999)
    expect(save).toHaveBeenCalledTimes(2)
    await vi.advanceTimersByTimeAsync(1)
    expect(save).toHaveBeenCalledTimes(3)

    await settle('failed')
    await vi.advanceTimersByTimeAsync(60_000)
    expect(save).toHaveBeenCalledTimes(4)

    await settle('failed')
    expect(last()).toEqual({ kind: 'failed', attempt: 4 })
    await vi.advanceTimersByTimeAsync(60_000)
    expect(save).toHaveBeenCalledTimes(5)
  })

  it('save_Throws_CountsAsFailed', async () => {
    // Arrange
    const statuses: SaveStatus[] = []
    const scheduler = createSaveScheduler({
      save: () => Promise.reject(new TypeError('Failed to fetch')),
      onStatus: (status) => statuses.push(status),
    })

    // Act
    scheduler.edit({ immediate: true })
    await vi.advanceTimersByTimeAsync(0)

    // Assert
    expect(statuses.at(-1)).toEqual({ kind: 'failed', attempt: 1 })
  })

  it('edit_AfterFailure_RetriesAtOnce', async () => {
    // Arrange
    const { save, settle, scheduler } = setup()
    scheduler.edit({ immediate: true })
    await settle('failed')

    // Act
    scheduler.edit()

    // Assert
    expect(save).toHaveBeenCalledTimes(2)
  })

  it('save_SucceedsAfterFailure_ResetsTheBackOff', async () => {
    // Arrange
    const { save, settle, scheduler } = setup()
    scheduler.edit({ immediate: true })
    await settle('failed')
    await vi.advanceTimersByTimeAsync(5_000)
    await settle('saved')

    // Act
    scheduler.edit({ immediate: true })
    await settle('failed')
    await vi.advanceTimersByTimeAsync(5_000)

    // Assert: back to the first, 5-second delay.
    expect(save).toHaveBeenCalledTimes(4)
  })

  it('save_Stopped_SendsNothingMoreUntilResumed', async () => {
    // Arrange
    const { save, settle, scheduler, last } = setup()
    scheduler.edit({ immediate: true })

    // Act
    await settle('stopped')
    scheduler.edit({ immediate: true })
    await vi.advanceTimersByTimeAsync(120_000)

    // Assert
    expect(last()).toEqual({ kind: 'stopped' })
    expect(save).toHaveBeenCalledTimes(1)
    scheduler.resume()
    expect(last()).toEqual({ kind: 'idle' })
    scheduler.edit({ immediate: true })
    expect(save).toHaveBeenCalledTimes(2)
  })

  it('resume_EditPendingWhenStopped_SavesAfterTheDebounce', async () => {
    // Arrange
    const { save, scheduler, last } = setup()
    scheduler.edit()
    await scheduler.stop()

    // Act
    scheduler.resume()

    // Assert
    expect(last()).toEqual({ kind: 'waiting' })
    await vi.advanceTimersByTimeAsync(2_000)
    expect(save).toHaveBeenCalledTimes(1)
  })

  it('flush_PendingEdit_SavesAtOnceAndResolvesTrue', async () => {
    // Arrange
    const { save, settle, scheduler } = setup()
    scheduler.edit()

    // Act
    const flushed = scheduler.flush()
    expect(save).toHaveBeenCalledTimes(1)
    await settle('saved')

    // Assert
    await expect(flushed).resolves.toBe(true)
  })

  it('flush_SaveInFlightWithLaterEdit_WaitsForBoth', async () => {
    // Arrange
    const { save, settle, scheduler } = setup()
    scheduler.edit({ immediate: true })
    scheduler.edit()

    // Act
    const flushed = scheduler.flush()
    await settle('saved')
    expect(save).toHaveBeenCalledTimes(2)
    await settle('saved')

    // Assert
    await expect(flushed).resolves.toBe(true)
  })

  it('flush_NothingPending_ResolvesTrueWithoutSaving', async () => {
    // Arrange
    const { save, scheduler } = setup()

    // Act
    const flushed = await scheduler.flush()

    // Assert
    expect(flushed).toBe(true)
    expect(save).not.toHaveBeenCalled()
  })

  it('flush_SaveFails_ResolvesFalseAndKeepsRetrying', async () => {
    // Arrange
    const { save, settle, scheduler } = setup()
    scheduler.edit()

    // Act
    const flushed = scheduler.flush()
    await settle('failed')

    // Assert
    await expect(flushed).resolves.toBe(false)
    await vi.advanceTimersByTimeAsync(5_000)
    expect(save).toHaveBeenCalledTimes(2)
  })

  it('stop_SaveInFlight_ResolvesAfterItAndCancelsTheDebounce', async () => {
    // Arrange
    const { save, settle, scheduler } = setup()
    scheduler.edit({ immediate: true })
    scheduler.edit()
    let stoppedYet = false

    // Act
    const stopping = scheduler.stop().then(() => {
      stoppedYet = true
    })
    await vi.advanceTimersByTimeAsync(0)
    const beforeSave = stoppedYet
    await settle('saved')
    await stopping
    await vi.advanceTimersByTimeAsync(10_000)

    // Assert
    expect(beforeSave).toBe(false)
    expect(stoppedYet).toBe(true)
    expect(save).toHaveBeenCalledTimes(1)
  })
})

describe('createIncompleteRowWatch', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('observe_SameRowForAMinute_ReportsIt', () => {
    // Arrange
    const onDue = vi.fn()
    const watch = createIncompleteRowWatch(onDue)

    // Act
    watch.observe('set-a:reps')
    vi.advanceTimersByTime(30_000)
    watch.observe('set-a:reps')
    vi.advanceTimersByTime(29_999)

    // Assert: the repeat didn't restart the clock.
    expect(onDue).not.toHaveBeenCalled()
    vi.advanceTimersByTime(1)
    expect(onDue).toHaveBeenCalledWith('set-a:reps')
  })

  it('observe_RowChanges_StartsTheMinuteAgain', () => {
    // Arrange
    const onDue = vi.fn()
    const watch = createIncompleteRowWatch(onDue)
    watch.observe('set-a:reps')
    vi.advanceTimersByTime(50_000)

    // Act
    watch.observe('set-b:weight')
    vi.advanceTimersByTime(50_000)

    // Assert
    expect(onDue).not.toHaveBeenCalled()
    vi.advanceTimersByTime(10_000)
    expect(onDue).toHaveBeenCalledWith('set-b:weight')
  })

  it('observe_RowCompletedAfterNotice_ClearsIt', () => {
    // Arrange
    const onDue = vi.fn()
    const watch = createIncompleteRowWatch(onDue)
    watch.observe('set-a:reps')
    vi.advanceTimersByTime(60_000)

    // Act
    watch.observe(null)

    // Assert
    expect(onDue).toHaveBeenLastCalledWith(null)
    expect(onDue).toHaveBeenCalledTimes(2)
  })

  it('observe_RowCompletedBeforeNotice_SaysNothing', () => {
    // Arrange
    const onDue = vi.fn()
    const watch = createIncompleteRowWatch(onDue)
    watch.observe('set-a:reps')
    vi.advanceTimersByTime(20_000)

    // Act
    watch.observe(null)
    vi.advanceTimersByTime(120_000)

    // Assert
    expect(onDue).not.toHaveBeenCalled()
  })
})
