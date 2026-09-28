namespace GymNotebook.Api;

// `optionalDetails` in GET /account/privacy (contracts/api.md → Account privacy state).
// TransitionPending is derived, never stored: no consent, but the account still holds an
// optional detail from before the feature (data-model.md). Only then does the UI ask the
// transition question — accounts without details are never prompted (FR-031).
public record OptionalDetailsStateResponse(
    string CurrentStatementVersion,
    OptionalDetailsConsentResponse? Consent,
    bool TransitionPending);
