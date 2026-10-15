namespace GymNotebook.Api;

// POST /workouts/{id}/template: only the name; the blocks come from the page.
public record SaveAsTemplateRequest(string? Name);
