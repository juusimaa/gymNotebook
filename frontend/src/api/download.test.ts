import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  IncompleteDownloadError,
  readCompleteJson,
  REVOKE_DELAY_MS,
  revokePendingDownloads,
  saveBlob,
  type DownloadEnvironment,
} from './download'

// The download helper without a browser: Response and Blob exist in Node, and
// the object-URL and click side is a fake environment that records what it was
// asked to do. Scheduled callbacks are collected rather than timed, so a test
// decides when "later" happens.

function fakeEnvironment() {
  const scheduled: (() => void)[] = []
  let next = 0
  const env = {
    createObjectURL: vi.fn(() => `blob:test/${++next}`),
    revokeObjectURL: vi.fn(),
    startSave: vi.fn(),
    schedule: vi.fn((callback: () => void, delayMs: number) => {
      expect(delayMs).toBe(REVOKE_DELAY_MS)
      scheduled.push(callback)
    }),
  } satisfies DownloadEnvironment
  return { env, runScheduled: () => scheduled.forEach((run) => run()) }
}

beforeEach(() => {
  // Module-level state: make sure no URL from an earlier test is still pending.
  revokePendingDownloads(fakeEnvironment().env)
})

describe('readCompleteJson', () => {
  it('returns the body as a JSON blob when it is one complete document', async () => {
    const blob = await readCompleteJson(new Response('{"formatVersion":1}'))

    expect(blob.type).toBe('application/json')
    expect(await blob.text()).toBe('{"formatVersion":1}')
  })

  it('rejects a body that ended before the closing brace', async () => {
    await expect(
      readCompleteJson(new Response('{"formatVersion":1,"sets":[{')),
    ).rejects.toBeInstanceOf(IncompleteDownloadError)
  })

  it('rejects with the stream error when the connection breaks mid-body', async () => {
    const broken = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new TextEncoder().encode('{"formatVersion":1,'))
        controller.error(new TypeError('network error'))
      },
    })

    await expect(readCompleteJson(new Response(broken))).rejects.toThrow(
      'network error',
    )
  })

  it('rejects when the request is aborted while the body is read', async () => {
    const controller = new AbortController()
    const body = new ReadableStream<Uint8Array>({
      start(stream) {
        stream.enqueue(new TextEncoder().encode('{'))
        controller.signal.addEventListener('abort', () =>
          stream.error(new DOMException('aborted', 'AbortError')),
        )
      },
    })
    const reading = readCompleteJson(new Response(body))

    controller.abort()

    await expect(reading).rejects.toMatchObject({ name: 'AbortError' })
  })
})

describe('saveBlob', () => {
  it('saves under the given name and revokes the URL once the delay passes', () => {
    const { env, runScheduled } = fakeEnvironment()

    saveBlob(new Blob(['{}']), 'gym-notebook-export.json', env)

    expect(env.startSave).toHaveBeenCalledWith(
      'blob:test/1',
      'gym-notebook-export.json',
    )
    expect(env.revokeObjectURL).not.toHaveBeenCalled()
    runScheduled()
    expect(env.revokeObjectURL).toHaveBeenCalledExactlyOnceWith('blob:test/1')
  })

  it('revokes at once on cancel, and the later timer does not revoke twice', () => {
    const { env, runScheduled } = fakeEnvironment()
    saveBlob(new Blob(['{}']), 'a.json', env)

    revokePendingDownloads(env)
    runScheduled()

    expect(env.revokeObjectURL).toHaveBeenCalledExactlyOnceWith('blob:test/1')
  })

  it('still schedules the revoke when starting the save throws', () => {
    const { env, runScheduled } = fakeEnvironment()
    env.startSave.mockImplementation(() => {
      throw new Error('blocked')
    })

    expect(() => saveBlob(new Blob(['{}']), 'a.json', env)).toThrow('blocked')
    runScheduled()

    expect(env.revokeObjectURL).toHaveBeenCalledExactlyOnceWith('blob:test/1')
  })
})
