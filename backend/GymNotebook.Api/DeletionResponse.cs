namespace GymNotebook.Api;

// The success body of POST /account/delete (specs/001 contracts/api.md → Deletion
// response). Deliberately minimal: the account no longer exists, so nothing personal is
// returned and no new token is issued. The dates let the completion screen say when the
// last copies expire:
//
//   - RetentionBoundaryAt: the deletion boundary, taken just before the transaction's
//     deletes, not the commit time. Retention is counted from it, so a slow commit can
//     never push a deadline later (data-model.md → Deletion log lines).
//   - BackupsExpireBy: boundary + 30 days, the backup limit.
//   - DeletionEvidenceExpiresBy: boundary + 31 days, when the deletion log lines are gone.
//     They sit under the 30-day log retention, plus a day for the provider's purge lag
//     (research R7).
public record DeletionResponse(
    string Status,
    DateTimeOffset RetentionBoundaryAt,
    DateTimeOffset BackupsExpireBy,
    DateTimeOffset DeletionEvidenceExpiresBy,
    string LogRetentionNotice);
