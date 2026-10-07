const MONTHS = [
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
]

const LONG_MONTHS = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
]

interface WorkoutSetFigure {
  reps: number
  weight: number | null
  isWarmup: boolean
}

export function formatWorkoutDate(date: string): {
  day: string
  month: string
} {
  const [, month, day] = date.split('-')

  return {
    day,
    month: MONTHS[Number(month) - 1],
  }
}

export function formatWorkoutLongDate(date: string): string {
  const [year, month, day] = date.split('-')
  return `${Number(day)} ${LONG_MONTHS[Number(month) - 1]} ${year}`
}

export function formatWorkoutTime(
  timestamp: string,
  timeZone?: string,
): string {
  // English UI copy with Finland's local time convention: a 24-hour clock and
  // a full stop separator (for example, 07.15).
  return new Intl.DateTimeFormat('en-FI', {
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
    timeZone,
  }).format(new Date(timestamp))
}

// The local calendar day of an instant, as numbers. Intl rather than
// getDate() so tests can pin the time zone the way formatWorkoutTime's do.
function localDayParts(
  timestamp: string,
  timeZone?: string,
): { year: number; month: number; day: number } {
  const parts = new Intl.DateTimeFormat('en-FI', {
    year: 'numeric',
    month: 'numeric',
    day: 'numeric',
    timeZone,
  }).formatToParts(new Date(timestamp))
  const part = (type: 'year' | 'month' | 'day') =>
    Number(parts.find((p) => p.type === type)?.value)
  return { year: part('year'), month: part('month'), day: part('day') }
}

// A finished session's "07.15–08.40". When the end falls on another day than
// the start — after midnight, or a page finished days late — the end's date
// follows it ("23.30–00.15 16 Sep"), so the range never passes off a long gap
// as a short session. The year joins only when that differs too.
export function formatWorkoutTimeRange(
  startedAt: string,
  endedAt: string,
  timeZone?: string,
): string {
  const range = `${formatWorkoutTime(startedAt, timeZone)}–${formatWorkoutTime(endedAt, timeZone)}`
  const start = localDayParts(startedAt, timeZone)
  const end = localDayParts(endedAt, timeZone)
  if (
    start.year === end.year &&
    start.month === end.month &&
    start.day === end.day
  ) {
    return range
  }
  const year = start.year === end.year ? '' : ` ${end.year}`
  return `${range} ${end.day} ${MONTHS[end.month - 1]}${year}`
}

// A day from a timestamp, written out in the interface's English: "4 October
// 2026". Used for the privacy screens' effective and acknowledgement dates.
// The locale is fixed rather than the browser's, so a Finnish browser doesn't
// turn one line of an English screen into "4. lokakuuta 2026"; 'en-FI' matches
// the clock convention formatWorkoutTime uses.
export function formatInstantDate(instant: string): string {
  return new Intl.DateTimeFormat('en-FI', { dateStyle: 'long' }).format(
    new Date(instant),
  )
}

export function formatCount(count: number, singular: string): string {
  return `${count} ${count === 1 ? singular : `${singular}s`}`
}

export function formatSetLoad(
  isBodyweight: boolean,
  weight: number | null,
  reps: number,
): string {
  if (isBodyweight) {
    return weight === null ? `bodyweight × ${reps}` : `+${weight} kg × ${reps}`
  }

  return weight === null
    ? `weight not logged × ${reps}`
    : `${weight} kg × ${reps}`
}

export function formatBestSet(
  isBodyweight: boolean,
  sets: WorkoutSetFigure[],
): string {
  const workingSets = sets.filter((set) => !set.isWarmup)

  if (workingSets.length === 0) {
    return 'warm-ups only'
  }

  if (isBodyweight) {
    const bestReps = Math.max(...workingSets.map((set) => set.reps))
    return `best ${bestReps} ${bestReps === 1 ? 'rep' : 'reps'}`
  }

  const estimates = workingSets.flatMap((set) => {
    if (set.weight === null) {
      return []
    }

    // A tested single is already an observed maximum. Applying Epley to it
    // would inflate the figure instead of reporting the weight actually lifted.
    return [set.reps === 1 ? set.weight : set.weight * (1 + set.reps / 30)]
  })

  return estimates.length === 0
    ? 'weight not logged'
    : `e1RM ${Math.round(Math.max(...estimates))} kg`
}
