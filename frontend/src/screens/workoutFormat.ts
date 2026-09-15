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

export function formatCount(count: number, singular: string): string {
  return `${count} ${count === 1 ? singular : `${singular}s`}`
}
