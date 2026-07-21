using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace OrderService.IntegrationTests;

/// <summary>
/// Its own CustomWebApplicationFactory instance — deliberately NOT sharing
/// OrdersApiTests' fixture. The "auth" rate limit policy's window is
/// per-process state; sharing a fixture with tests that also call
/// /auth/register would make this test's deliberate limit-exhaustion leak
/// into (and randomly 429) unrelated tests depending on run order.
/// </summary>
public class RateLimitingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public RateLimitingTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PostAuthRegister_MoreThanFivePerMinuteFromSameCaller_Returns429OnTheSixth()
    {
        // The "auth" policy (Program.cs) allows 5 requests/minute per
        // caller. TestServer gives every request from this HttpClient the
        // same simulated remote IP, so they all land in the same partition.
        HttpResponseMessage? lastResponse = null;

        for (var i = 0; i < 6; i++)
        {
            lastResponse = await _client.PostAsJsonAsync("/api/v1/auth/register",
                new { email = $"ratelimit-{Guid.NewGuid():N}@test.se", password = "Secret123!" });
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse!.StatusCode);
    }
}
