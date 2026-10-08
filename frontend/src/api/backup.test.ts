import { describe, expect, it } from 'vitest'
import { backupFileName } from './backup'

describe('backupFileName', () => {
  it('dates the file by the local calendar day', () => {
    // Late evening local time: the day the user sees, not the UTC one.
    const now = new Date(2026, 9, 8, 23, 59)

    expect(backupFileName('json', now)).toBe('gym-notebook-2026-10-08.json')
  })

  it('pads month and day and names a spreadsheet .csv', () => {
    const now = new Date(2027, 0, 3, 0, 1)

    expect(backupFileName('csv', now)).toBe('gym-notebook-2027-01-03.csv')
  })
})
