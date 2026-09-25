using GymNotebook.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GymNotebook.Tests;

// Research R4 → Q3: every endpoint that requires authorization must run under the
// account-lifecycle filter, including routes added later — a new route that forgets it
// would silently race account deletion. So instead of trusting each route to remember,
// this enumerates every mapped endpoint and fails on any authorized one that lacks the
// filter's marker metadata (added together with the filter by RequireAccountLifecycle()).
public class LifecycleCoverageTests(PrivacyEnabledGymNotebookFactory factory) : IClassFixture<PrivacyEnabledGymNotebookFactory>
{
    // Endpoints that take their own guard instead of the shared filter, each with the
    // reason. The lock tests (ChangePassword*, LifecycleCoordinationTests and the US3/US4
    // suites; ExportCoordinationTests for export) prove each of these really does take its
    // own guard. Delete doesn't exist yet; listing it now means it can't be added without
    // passing this review.
    private static readonly Dictionary<string, string> _allowList = new()
    {
        ["POST /account/export"] = "Own snapshot guards: a short initialization guard, then a delivery guard per chunk outside the REPEATABLE READ snapshot (research R3/R4).",
        ["POST /auth/change-password"] = "Takes exclusive access in its own transaction; wrapping it in the shared filter would need a shared-to-exclusive upgrade, which deadlocks two upgraders (analysis I1).",
        ["POST /account/delete"] = "Takes exclusive access in its own transaction, and owns its commit for the deletion log lines (analysis I1, research R6).",
    };

    [Fact]
    public void AuthorizedEndpoints_WithoutLifecycleFilter_OnlyAllowListed()
    {
        // Arrange: the privacy-enabled host, so feature routes mapped behind the flag are
        // enumerated too.
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

        // Act
        var authorized = endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is not null && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .ToList();
        var unguarded = authorized
            .Where(e => e.Metadata.GetMetadata<AccountLifecycleGuardMetadata>() is null)
            .SelectMany(Describe)
            .Where(route => !_allowList.ContainsKey(route))
            .ToList();

        // Assert: not vacuous — the notebook routes are there and guarded — and nothing
        // authorized is left unguarded outside the allow-list.
        Assert.Contains(authorized, e => e.Metadata.GetMetadata<AccountLifecycleGuardMetadata>() is not null);
        Assert.Empty(unguarded);
    }

    [Fact]
    public void GuardedEndpoints_Include_NotebookRoutesAndMe()
    {
        // Arrange
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

        // Act
        var guarded = endpoints
            .Where(e => e.Metadata.GetMetadata<AccountLifecycleGuardMetadata>() is not null)
            .SelectMany(Describe)
            .ToHashSet();

        // Assert: spot checks of the routes T020 names.
        Assert.Contains("GET /auth/me", guarded);
        Assert.Contains("GET /exercises/", guarded);
        Assert.Contains("GET /workouts/{id:int}", guarded);
        Assert.Contains("PUT /workouts/{id:int}/exercises", guarded);
        Assert.Contains("POST /workouts/{id:int}/sets", guarded);
        Assert.DoesNotContain("POST /auth/change-password", guarded);
        Assert.DoesNotContain("POST /account/export", guarded);

        // US1's account privacy routes (T031), mapped because this host has the flag on.
        Assert.Contains("PUT /account/privacy/acknowledgement", guarded);
    }

    // "METHOD /route" for each HTTP method the endpoint answers.
    private static IEnumerable<string> Describe(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
        return methods.Select(method => $"{method} {endpoint.RoutePattern.RawText}");
    }
}
