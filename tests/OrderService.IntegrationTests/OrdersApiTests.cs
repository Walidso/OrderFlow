using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using OrderFlow.Contracts;
using Xunit;

namespace OrderService.IntegrationTests;

/// <summary>
/// Real HTTP requests against the in-memory app. These tests exercise the
/// FULL stack: routing -> JWT middleware -> model binding -> validation
/// pipeline -> handler -> EF -> error middleware. A unit test can't tell
/// you a middleware is in the wrong order — these can.
/// </summary>
public class OrdersApiTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public OrdersApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<string> RegisterAndGetTokenAsync()
    {
        // Unique email per call so tests never collide on the unique index.
        var response = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { email = $"user-{Guid.NewGuid():N}@test.se", password = "Secret123!" });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.Token;
    }

    private sealed record AuthResponse(string Token, string Email);

    [Fact]
    public async Task PostOrder_WithoutToken_Returns401()
    {
        // No Authorization header at all — the JWT middleware must stop us
        // before ANY handler code runs. This test would catch a forgotten
        // [Authorize] attribute, which no unit test ever could.
        var response = await _client.PostAsJsonAsync("/api/v1/orders",
            new { items = new[] { new { productId = "APPLE-1", productName = "Apple", quantity = 1, unitPrice = 25.0 } } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostOrder_EmptyItems_Returns400ProblemDetails()
    {
        var token = await RegisterAndGetTokenAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.PostAsJsonAsync("/api/v1/orders",
            new { items = Array.Empty<object>() }); // validator demands >= 1 item

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The error middleware must produce RFC 7807 output:
        Assert.Equal("application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("Items", problem); // per-field validation error present
    }

    [Fact]
    public async Task PostOrder_HappyPath_Returns201_AndPublishesOrderCreated()
    {
        var token = await RegisterAndGetTokenAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.PostAsJsonAsync("/api/v1/orders", new
        {
            items = new[]
            {
                new { productId = "APPLE-1", productName = "Apple", quantity = 3, unitPrice = 25.0 }
            }
        });

        // 201 + Location header = REST-correct creation.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        // And the event MUST have hit the bus — otherwise Inventory never
        // hears about the order and it stays Pending forever.
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        Assert.True(await harness.Published.Any<OrderCreated>());
    }
}
