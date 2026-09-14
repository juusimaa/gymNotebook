namespace GymNotebook.Api;

// Body of POST /auth/change-password. No username: the caller is already identified by
// their bearer token, so all that's needed is proof they know the current password and
// the one to replace it with.
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
