namespace GymNotebook.Api;

// Body of GET /health. A named type rather than an anonymous object so the OpenAPI
// generator can put a real schema in the document (see the /health endpoint in Program.cs).
public record HealthResponse(string Status, string Database);
