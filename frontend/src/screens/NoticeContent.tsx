import type { PrivacyNotice } from '../api/privacy'
import './Privacy.css'

// The body of a privacy notice, shared by the public /privacy screen and the
// notebook gate so both show exactly the same text for a version. Everything
// is rendered as React text nodes — no dangerouslySetInnerHTML anywhere — so a
// notice can't carry markup onto the page (contracts/api.md → Notice document).

// Dates only: the notice's instants are UTC midnights in practice, and the
// reader needs the day, formatted in their own locale.
function formatNoticeDate(instant: string): string {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'long' }).format(
    new Date(instant),
  )
}

export default function NoticeContent({ notice }: { notice: PrivacyNotice }) {
  const successor = notice.announcedSuccessor

  return (
    <>
      <p className="notice-meta num">
        Version {notice.version} · effective{' '}
        {formatNoticeDate(notice.effectiveAt)}
      </p>
      {/* What this version changed — on the gate, the reason it's shown again. */}
      <p className="muted">{notice.materialChangeSummary}</p>

      <div className="notice-sections">
        {notice.sections.map((section) => (
          // id="notice-contact" etc. lets other screens link to a section.
          <section
            key={section.id}
            id={`notice-${section.id}`}
            className="notice-section"
          >
            <h3>{section.heading}</h3>
            {section.paragraphs.map((paragraph, i) => (
              // Paragraphs have no ids of their own and never reorder within
              // a version, so the index is a stable key.
              <p key={i}>{paragraph}</p>
            ))}
          </section>
        ))}
      </div>

      {/* FR-003: a material change is announced before it takes effect. The
          full text sits in a native <details>, which is keyboard-operable and
          announced as expandable without any script. */}
      {successor !== null && (
        <aside className="notice-announced" aria-labelledby="notice-announced">
          <h3 id="notice-announced">
            Announced change, effective{' '}
            {formatNoticeDate(successor.effectiveAt)}
          </h3>
          <p>{successor.materialChangeSummary}</p>
          <details>
            <summary>Read version {successor.version}</summary>
            {successor.sections.map((section) => (
              <section key={section.id} className="notice-section">
                <h3>{section.heading}</h3>
                {section.paragraphs.map((paragraph, i) => (
                  <p key={i}>{paragraph}</p>
                ))}
              </section>
            ))}
          </details>
        </aside>
      )}
    </>
  )
}
