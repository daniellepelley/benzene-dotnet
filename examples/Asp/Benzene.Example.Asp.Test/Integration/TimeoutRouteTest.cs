using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Benzene.Example.Asp.Test.Integration;

/// <summary>
/// Covers the Asp example's cancellation demo - the <c>/slow</c> branch wired in
/// <c>Startup.Configure</c> with <c>.UseTimeout(TimeSpan.FromSeconds(2))</c> in front of
/// <c>SlowOperationMessageHandler</c>, whose simulated downstream call takes 5 seconds and reads the
/// ambient cancellation token. The deadline must win: the caller gets a <c>timeout</c> failure result
/// (HTTP 504, an RFC 9457 problem document) well before the 5-second call would have finished, rather
/// than waiting it out or seeing an aborted connection.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class TimeoutRouteTest : InMemoryOrdersTestBase
{
    [Fact]
    public async Task SlowOperation_PastItsDeadline_IsATimeoutResult()
    {
        var stopwatch = Stopwatch.StartNew();

        var response = await _client.GetAsync("/slow/slow-operation");

        stopwatch.Stop();
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("timeout", problem.RootElement.GetProperty("benzeneStatus").GetString());
        // The handler's call takes 5 s; the 2 s deadline must have cut it short.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4.5), $"took {stopwatch.Elapsed}");
    }
}
