namespace GymNotebook.Api;

// GET /account/privacy (contracts/api.md → Account privacy state). RequiresAcknowledgement
// is decided on the server — the client never compares versions itself — and is true when
// the account has no acknowledgement or acknowledged an older version. No password hash,
// token version or other revocation values.
public record AccountPrivacyResponse(
    string CurrentNoticeVersion,
    NoticeAcknowledgementResponse? Acknowledgement,
    bool RequiresAcknowledgement);
