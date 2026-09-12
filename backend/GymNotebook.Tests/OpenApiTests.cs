namespace GymNotebook.Tests;

public class OpenApiTests(GymNotebookFactory factory) : IClassFixture<GymNotebookFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task OpenApi_document_describes_health_endpoint()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"/health\"", json);
        Assert.Contains("Gym Notebook API", json);
    }
}