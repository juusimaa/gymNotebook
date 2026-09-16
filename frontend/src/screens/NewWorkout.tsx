import { Link } from 'react-router'
import './NewWorkout.css'

export default function NewWorkout() {
  return (
    <main className="page new-workout">
      <header className="new-workout-header">
        <Link to="/workouts">← Cancel</Link>
        <h1>New page</h1>
        <span className="new-workout-header-spacer" aria-hidden="true"></span>
      </header>
      <div className="new-workout-content">
        <p>Workout editor goes here.</p>
      </div>
    </main>
  )
}
