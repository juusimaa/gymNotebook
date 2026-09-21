import { useState } from 'react'
import { Link } from 'react-router'
import {
  createInitialHeadingDraft,
  type WorkoutHeadingDraft,
} from './newWorkoutDraft'
import './NewWorkout.css'

export default function NewWorkout() {
  // Lazy initialization captures the local date and time once when the screen mounts.
  const [heading, setHeading] = useState(() =>
    createInitialHeadingDraft(new Date()),
  )

  // `keyof` limits callers to fields that actually exist in WorkoutHeadingDraft.
  function updateHeadingField(field: keyof WorkoutHeadingDraft, value: string) {
    setHeading((current) => ({ ...current, [field]: value }))
  }

  return (
    <main className="page new-workout">
      <header className="new-workout-header">
        <Link to="/workouts">← Cancel</Link>
        <h1>New page</h1>
        <span className="new-workout-header-spacer" aria-hidden="true"></span>
      </header>
      <div className="new-workout-content">
        <section className="form-stack" aria-labelledby="heading-title">
          <h2 id="heading-title">Page heading</h2>
          <div className="field">
            <label className="label" htmlFor="workout-date">
              Date
            </label>
            <input
              className="input"
              id="workout-date"
              type="date"
              required
              value={heading.date}
              onChange={(event) =>
                updateHeadingField('date', event.target.value)
              }
            />
          </div>
          <div className="field">
            <label className="label" htmlFor="startTime">
              Started
            </label>
            <input
              className="input"
              id="startTime"
              type="time"
              required
              value={heading.startTime}
              onChange={(event) =>
                updateHeadingField('startTime', event.target.value)
              }
            />
          </div>
          <div className="field">
            <label className="label" htmlFor="title">
              Title
            </label>
            <input
              className="input"
              id="title"
              type="text"
              value={heading.title}
              onChange={(event) =>
                updateHeadingField('title', event.target.value)
              }
            />
          </div>
          <div className="field">
            <label className="label" htmlFor="bodyweightKg">
              Bodyweight (kg)
            </label>
            <input
              className="input"
              id="bodyweightKg"
              type="text"
              inputMode="decimal"
              value={heading.bodyweightKg}
              onChange={(event) =>
                updateHeadingField('bodyweightKg', event.target.value)
              }
            />
          </div>
          <div className="field">
            <label className="label" htmlFor="location">
              Gym
            </label>
            <input
              className="input"
              id="location"
              type="text"
              value={heading.location}
              onChange={(event) =>
                updateHeadingField('location', event.target.value)
              }
            />
          </div>
          <div className="field">
            <label className="label" htmlFor="notes">
              Notes
            </label>
            <textarea
              className="input"
              id="notes"
              value={heading.notes}
              onChange={(event) =>
                updateHeadingField('notes', event.target.value)
              }
            />
          </div>
        </section>
      </div>
    </main>
  )
}
