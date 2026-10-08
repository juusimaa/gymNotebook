namespace GymNotebook.Api;

// The password is compared exactly as sent. Both formats stream the same JSON;
// CSV is converted by the browser and must not count as a restorable full backup.
// An omitted format preserves the original export client's behavior.
public record ExportRequest(string CurrentPassword, string Format = "json");
