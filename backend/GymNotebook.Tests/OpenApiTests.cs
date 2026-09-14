namespace GymNotebook.Tests;

// The document is only mapped in Development; WebApplicationFactory boots the app as
// Development by default, which is why this gets a 200 at all.
public class OpenApiTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task OpenApi_document_describes_health_endpoint()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        // Plain string checks rather than a parsed document keep the test free of a
        // Microsoft.OpenApi reader dependency. It only needs to prove the generator ran
        // (/health is in it) and the title transformer touched the output.
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"/health\"", json);
        Assert.Contains("Gym Notebook API", json);
    }
}
