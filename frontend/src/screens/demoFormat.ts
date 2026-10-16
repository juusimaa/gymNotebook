import { describeAuthError, isCaptchaFailure } from '../api/authErrors'
import { ApiError } from '../api/client'
import {
  formatInstantDate,
  formatWorkoutTime,
  localDayParts,
} from './workoutFormat'

// Copy for the demo screens (docs/ui/README.md, screens 1 and 2). Pure, so
// Vitest tests it directly; timeZone is there for the tests to pin.

// When the demo ends, for the cover's badge: "today at 21.40" or "tomorrow at
// 18.30". A demo lasts 24 hours, so it is always one of the two; anything else
// (a wrong device clock) falls back to the date, so the line is never wrong.
export function formatDemoEnd(
  expiresAt: string,
  now: Date,
  timeZone?: string,
): string {
  const time = formatWorkoutTime(expiresAt, timeZone)
  const days = dayNumber(expiresAt, timeZone) - dayNumber(now, timeZone)
  if (days === 0) return `today at ${time}`
  if (days === 1) return `tomorrow at ${time}`
  return `on ${formatInstantDate(expiresAt)} at ${time}`
}

// Days since 1970 of an instant's local calendar day, for comparing days
// without the clock time getting in the way.
function dayNumber(instant: string | Date, timeZone?: string): number {
  const iso = typeof instant === 'string' ? instant : instant.toISOString()
  const { year, month, day } = localDayParts(iso, timeZone)
  return Date.UTC(year, month - 1, day) / 86_400_000
}

// Why "Start the demo" failed, under the bot check.
export function describeDemoStartError(err: unknown): string {
  if (isCaptchaFailure(err)) {
    return "The check didn't pass. Try again."
  }
  if (err instanceof ApiError) {
    if (err.status === 503 && err.code === 'demo_unavailable') {
      return 'Too many demos are running right now. Try again in a few minutes.'
    }
    if (err.status === 429) {
      return 'Too many demos started from this network. Try again later.'
    }
    // The frontend's switch is on but the API's isn't: the route isn't mapped.
    if (err.status === 404) {
      return "The demo isn't available right now."
    }
    // invalid_request: the browser's UTC offset was refused. Nothing the
    // visitor typed, so describeAuthError's "check the email" would mislead.
    if (err.status === 400) {
      return "The demo couldn't start. Reload the page and try again."
    }
  }
  // The connection and "the server answered 500" copy the screen already uses.
  return describeAuthError(err)
}

// Why "Reset demo data" failed, inside its confirmation. A 401 never gets
// here: the demo has ended, and that signs out.
export function describeDemoResetError(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 503) {
      return 'The notebook is busy, so nothing was reset. Try again in a moment.'
    }
    if (err.status === 429) {
      return 'Too many resets. Wait a minute and try again.'
    }
    if (err.status === 403) {
      return "This account isn't a demo, so nothing was reset."
    }
  }
  // The reset is all or nothing, but a broken connection can't tell which
  // happened. Trying again is harmless either way: it resets once more.
  return 'The reset may not have finished. Open the notebook to check, or try again.'
}
