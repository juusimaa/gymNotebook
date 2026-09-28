namespace GymNotebook.Api;

// DELETE /account/privacy/optional-details-consent. How many workouts this request actually
// cleared: a retry after a lost response returns 0, never a repeat of the first count.
public record OptionalDetailsWithdrawalResponse(int ClearedWorkouts);
