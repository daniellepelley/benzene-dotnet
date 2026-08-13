namespace Benzene.ClaimCheck;

/// <summary>
/// An in-process <see cref="IClaimCheckStore"/> backed by a dictionary, suitable for a single worker
/// instance, tests, and local development. Issues references with the reserved <c>memory</c> scheme
/// (<c>memory://{topic}/{key}</c>).
/// </summary>
/// <remarks>
/// State lives in this process only. In a multi-instance deployment each instance keeps its own map,
/// so a payload offloaded by one instance is invisible to another - use a shared/durable store there
/// (e.g. the S3 or Azure Blob Storage claim-check store packages). Entries are held for a
/// configurable time-to-live and expired lazily on the next access to their key, the same shape as
/// <c>Benzene.Idempotency.InMemoryIdempotencyStore</c>.
/// </remarks>
public class InMemoryClaimCheckStore : IClaimCheckStore
{
    /// <summary>The reference scheme this store issues and accepts: <c>memory</c>.</summary>
    public const string Scheme = "memory";

    private sealed class Entry
    {
        public required string Body { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _gate = new();
    private readonly TimeSpan _timeToLive;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryClaimCheckStore"/> class.
    /// </summary>
    /// <param name="timeToLive">How long a stored payload is retained. Defaults to 24 hours.</param>
    /// <param name="now">A clock, overridable for tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public InMemoryClaimCheckStore(TimeSpan? timeToLive = null, Func<DateTimeOffset>? now = null)
    {
        _timeToLive = timeToLive ?? TimeSpan.FromHours(24);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public Task<string> PutAsync(string body, ClaimCheckPutContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = Guid.NewGuid().ToString("n");
        var reference = $"{Scheme}://{Uri.EscapeDataString(context.Topic)}/{key}";

        lock (_gate)
        {
            _entries[reference] = new Entry { Body = body, ExpiresAt = _now() + _timeToLive };
        }

        return Task.FromResult(reference);
    }

    /// <inheritdoc />
    /// <exception cref="ClaimCheckStoreMismatchException">
    /// <paramref name="reference"/> does not use the <see cref="Scheme"/> this store issues.
    /// </exception>
    public Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!reference.StartsWith(Scheme + "://", StringComparison.Ordinal))
        {
            throw new ClaimCheckStoreMismatchException(reference);
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(reference, out var entry) && entry.ExpiresAt > _now())
            {
                return Task.FromResult<string?>(entry.Body);
            }
        }

        return Task.FromResult<string?>(null);
    }
}
