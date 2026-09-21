// Form fields stay as strings so empty and partially entered values such as
// "78." remain representable until the draft is validated for submission.
export interface WorkoutHeadingDraft {
  date: string
  startTime: string
  title: string
  bodyweightKg: string
  location: string
  notes: string
}

function padTwoDigits(value: number): string {
  return value.toString().padStart(2, '0')
}

// Uses local date/time getters because the workout date is the calendar date
// chosen by the user; converting through UTC could shift it to another day.
export function createInitialHeadingDraft(now: Date): WorkoutHeadingDraft {
  const year = now.getFullYear()
  const month = padTwoDigits(now.getMonth() + 1)
  const day = padTwoDigits(now.getDate())
  const hours = padTwoDigits(now.getHours())
  const minutes = padTwoDigits(now.getMinutes())

  return {
    date: `${year}-${month}-${day}`,
    startTime: `${hours}:${minutes}`,
    title: '',
    bodyweightKg: '',
    location: '',
    notes: '',
  }
}
