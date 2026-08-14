using System;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Results;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Example.Asp.Cancellation;

public class SlowOperationResponse
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Demonstrates the ambient-cancellation model (see "Cancellation" in docs/message-handlers.md):
/// inject <see cref="ICancellationTokenAccessor"/> and read <c>.CancellationToken</c> at the point
/// of use - never cache it in a field at construction time - then pass it into your own I/O
/// exactly like you would on a raw <c>CancellationToken</c> parameter. The handler's own signature
/// never changes.
/// </summary>
/// <remarks>
/// Registered on the isolated <c>/slow</c> branch in <see cref="Startup"/>, which wraps its
/// pipeline in <c>.UseTimeout(TimeSpan.FromSeconds(2))</c> (<c>Benzene.Resilience</c>). Calling
/// this endpoint therefore times out before the simulated 5-second downstream call finishes, and
/// the response comes back as a <c>BenzeneResultStatus.Timeout</c> ("timeout") failure result
/// instead of the caller waiting out the full 5 seconds or getting an opaque aborted connection.
/// On any host that hasn't wired <c>.UseTimeout(...)</c> at all, this exact same handler still
/// works correctly - the accessor's token just defaults to whatever (if anything) the host itself
/// seeds, per the guarantee documented on <see cref="ICancellationTokenAccessor"/>.
/// </remarks>
[HttpEndpoint("GET", "/slow-operation")]
[Message("demo:slow-operation")]
public class SlowOperationMessageHandler : IMessageHandler<Void, SlowOperationResponse>
{
    private readonly ICancellationTokenAccessor _cancellation;

    public SlowOperationMessageHandler(ICancellationTokenAccessor cancellation)
    {
        _cancellation = cancellation;
    }

    public async Task<IBenzeneResult<SlowOperationResponse>> HandleAsync(Void request)
    {
        // Simulates a slow downstream call (an HTTP request, a DB query, ...). Reading
        // _cancellation.CancellationToken here - at the point of use, not captured earlier - is
        // what lets .UseTimeout(...) (or a real host shutdown) actually interrupt this call.
        await Task.Delay(TimeSpan.FromSeconds(5), _cancellation.CancellationToken);

        return BenzeneResult.Ok(new SlowOperationResponse
        {
            Message = "Completed without hitting the configured timeout."
        });
    }
}
