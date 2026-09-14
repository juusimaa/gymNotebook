namespace GymNotebook.Api;

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
