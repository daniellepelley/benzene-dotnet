using System.Diagnostics;
using System.Text;
using System.Threading;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;

namespace Benzene.ClaimCheck;

/// <summary>
/// Outbound middleware that offloads an oversized <see cref="OutboundContext.Request"/> to an
/// <see cref="IClaimCheckStore"/> and replaces it with a tiny <see cref="ClaimCheckPlaceholder"/>,
/// carrying the store-issued reference on the wire header named by
/// <see cref="ClaimCheckOptions.HeaderName"/>. See <c>work/archive/claim-check-plan-2026-08.md</c> §1 for the full
/// design reasoning.
/// </summary>
/// <remarks>
/// <para>
/// Place it on the <see cref="OutboundContext"/> route pipeline, after anything that mutates
/// <see cref="OutboundContext.Request"/> (nothing does today) and before the terminal transport
/// converter (<c>UseSqs</c>/<c>UseSns</c>/etc.) - it needs to see the final request and it must run
/// before the converter serializes whatever <see cref="OutboundContext.Request"/> ends up being:
/// <code>
/// routing.Route("payments:capture", pipeline =&gt; pipeline
///     .UseCorrelationId()
///     .UseClaimCheck()          // last non-terminal step
///     .UseSqs(queueUrl));
/// </code>
/// </para>
/// <para>
/// It measures size by serializing <see cref="OutboundContext.Request"/> itself with
/// <see cref="ClaimCheckOptions.Serializer"/> (default <see cref="JsonSerializer"/>) - the
/// serialization work is the price of measuring accurately rather than guessing from the typed
/// object, and it is also exactly the string that gets stored, so <see cref="IClaimCheckStore.GetAsync"/>
/// returns the identical wire body a non-offloaded send would have produced (subject to the
/// serializer-consistency caveat on <see cref="ClaimCheckOptions.Serializer"/>).
/// </para>
/// <para>
/// Transactional honesty: the store <c>PutAsync</c> happens before <c>next()</c> continues to the
/// transport send. If the put fails, this middleware throws and the send never happens - fail loud.
/// If the put succeeds but the subsequent send then fails, the stored payload is orphaned; the
/// store's own time-to-live is the cleanup mechanism (see <c>work/archive/claim-check-plan-2026-08.md</c> §3), not a
/// two-phase commit.
/// </para>
/// </remarks>
public class ClaimCheckOffloadMiddleware : IMiddleware<OutboundContext>
{
    private readonly IClaimCheckStore _store;
    private readonly ClaimCheckOptions _options;
    private readonly ISerializer _serializer;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaimCheckOffloadMiddleware"/> class.
    /// </summary>
    /// <param name="store">The store offloaded bodies are put into.</param>
    /// <param name="options">Threshold, header name, and serializer configuration.</param>
    /// <param name="cancellation">Supplies the ambient cancellation token to pass into the store call; null observes no cancellation.</param>
    public ClaimCheckOffloadMiddleware(IClaimCheckStore store, ClaimCheckOptions options, ICancellationTokenAccessor? cancellation = null)
    {
        _store = store;
        _options = options;
        _serializer = options.Serializer ?? new JsonSerializer();
        _cancellation = cancellation;
    }

    /// <inheritdoc />
    public string Name => nameof(ClaimCheckOffloadMiddleware);

    /// <inheritdoc />
    public async Task HandleAsync(OutboundContext context, Func<Task> next)
    {
        var body = _serializer.Serialize(context.Request);
        var byteCount = Encoding.UTF8.GetByteCount(body);

        if (!_options.AlwaysOffload && byteCount < _options.ThresholdBytes)
        {
            await next();
            return;
        }

        // Read at the point of use (not captured earlier): the ambient token can be replaced by a
        // wrapping middleware (e.g. a timeout) for the duration of an inner call.
        var token = _cancellation?.CancellationToken ?? CancellationToken.None;
        var reference = await _store.PutAsync(body, new ClaimCheckPutContext(context.Topic), token);

        context.Headers[_options.HeaderName] = reference;
        context.Request = new ClaimCheckPlaceholder(reference);

        var activity = Activity.Current;
        if (activity != null)
        {
            activity.SetTag("benzene.claim-check", "offloaded");
            activity.SetTag("benzene.claim-check.bytes", byteCount);
        }

        await next();
    }
}
