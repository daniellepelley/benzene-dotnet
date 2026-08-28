using System.Threading.RateLimiting;

namespace Benzene.RateLimiting;

/// <summary>
/// A tiny DI-owned wrapper around an internally-created <see cref="RateLimiter"/>, whose only job
/// is to give the DI container something it will actually dispose (#249). See
/// <c>Extensions.UseInternallyOwnedRateLimiting</c> for the full mechanism and why this needs to be
/// a distinct type rather than registering <see cref="RateLimiter"/> itself.
/// </summary>
internal sealed class OwnedRateLimiter : IAsyncDisposable
{
    /// <summary>Wraps the limiter this package created on the caller's behalf.</summary>
    /// <param name="rateLimiter">The limiter to dispose when this wrapper is disposed.</param>
    public OwnedRateLimiter(RateLimiter rateLimiter)
    {
        RateLimiter = rateLimiter;
    }

    /// <summary>The wrapped limiter, for the one caller (the middleware factory closure in <c>Extensions.cs</c>) that forces this type's resolution.</summary>
    public RateLimiter RateLimiter { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => RateLimiter.DisposeAsync();
}
