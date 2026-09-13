namespace GymNotebook.Api;

public record RegisterRequest(string Username, string Password, string? InviteCode);