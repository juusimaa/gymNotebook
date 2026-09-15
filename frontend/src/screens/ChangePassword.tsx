import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { changePassword } from '../api/auth'
import { describeAuthError } from '../api/authErrors'
import { ApiError } from '../api/client'
import { setToken } from '../auth/token'

function describeError(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 401) return 'Current password is wrong'
    if (err.status === 400) return 'Both passwords are required'
  }
  return describeAuthError(err)
}

export default function ChangePassword() {
  const navigate = useNavigate()
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [message, setMessage] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function submit() {
    setMessage(null)
    setSubmitting(true)
    try {
      const { token } = await changePassword({ currentPassword, newPassword })
      setToken(token)
      void navigate('/')
    } catch (err) {
      setMessage(describeError(err))
      setCurrentPassword('')
    } finally {
      setSubmitting(false)
    }
  }
  return (
    <main className="page">
      <p className="kicker">Training log</p>
      <h2>Change password</h2>

      <form
        className="form-stack"
        onSubmit={(e) => {
          e.preventDefault()
          void submit()
        }}
      >
        <label className="field">
          <span className="label">Current password</span>
          <input
            className="input"
            type="password"
            value={currentPassword}
            onChange={(e) => setCurrentPassword(e.target.value)}
            autoComplete="current-password"
            required
          />
        </label>
        <label className="field">
          <span className="label">New password</span>
          <input
            className="input"
            type="password"
            value={newPassword}
            onChange={(e) => setNewPassword(e.target.value)}
            autoComplete="new-password"
            required
          />
        </label>

        <button
          className="btn btn-primary btn-block"
          type="submit"
          disabled={submitting}
        >
          Change password
        </button>
        <p className="muted" style={{ textAlign: 'center', margin: 0 }}>
          <Link to="/" className="btn btn-ghost">
            Back to the cover
          </Link>
        </p>

        {message !== null && (
          <p className="form-message" role="alert">
            {message}
          </p>
        )}
      </form>
    </main>
  )
}
