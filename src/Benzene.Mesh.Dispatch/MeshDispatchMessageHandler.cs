using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.Messages;
using Benzene.Mesh.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Mesh.Dispatch;

/// <summary>
/// Serves the <c>mesh:dispatch</c> topic: invokes ONE registered service's real handler with a
/// caller-supplied payload and returns its response. Off unless <see cref="MeshDispatchGate.IsAllowed"/>
/// (opt-in registration AND non-Production / AllowInProduction) - a real handler runs, with real
/// side-effects. This is the direct-to-consumer F3b path; it reuses the same access the aggregator
/// already uses to interrogate each service (HTTP GET / Lambda Invoke), changing the payload, not the
/// permission, and is bounded to one declared service - never a shared queue.
/// </summary>
public class MeshDispatchMessageHandler : IMessageHandler<MeshDispatchRequest, RawStringMessage>
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly MeshDispatchGate _gate;
    private readonly MeshServiceRegistry _registry;
    private readonly IReadOnlyList<IMeshServiceDispatcher> _dispatchers;
    private readonly MeshDispatchGuardOptions _guardOptions;
    private readonly MeshDispatchRateLimiter _limiter;
    private readonly MeshDispatchIdentity _identity;
    private readonly ILogger? _logger;
    private readonly ICancellationTokenAccessor? _cancellation;

    /// <summary>Initializes a new instance of the <see cref="MeshDispatchMessageHandler"/> class.</summary>
    /// <remarks>
    /// The guard collaborators are optional so the handler still works when only
    /// <c>UseMeshDispatch()</c> is wired (no HTTP surface, no guard): with no limiter there is no
    /// per-target bound and with no identity the audit record says the caller was unattributed —
    /// both stated rather than silently skipped. <paramref name="cancellation"/> is likewise optional -
    /// the same <see cref="ICancellationTokenAccessor"/> idiom <c>HttpBenzeneMessageClient</c> uses
    /// (<c>src/Benzene.Clients.Http</c>): when nothing seeded it, or nothing is registered at all, the
    /// dispatch observes no cancellation - identical to today's behaviour (#185).
    /// </remarks>
    public MeshDispatchMessageHandler(
        MeshDispatchGate gate, MeshServiceRegistry registry, IEnumerable<IMeshServiceDispatcher> dispatchers,
        MeshDispatchGuardOptions? guardOptions = null, MeshDispatchRateLimiter? limiter = null,
        MeshDispatchIdentity? identity = null, ILogger<MeshDispatchMessageHandler>? logger = null,
        ICancellationTokenAccessor? cancellation = null)
    {
        _gate = gate;
        _registry = registry;
        _dispatchers = dispatchers.ToArray();
        _guardOptions = guardOptions ?? new MeshDispatchGuardOptions();
        _limiter = limiter ?? new MeshDispatchRateLimiter();
        _identity = identity ?? new MeshDispatchIdentity();
        _logger = logger;
        _cancellation = cancellation;
    }

    /// <summary>
    /// The audit record for one dispatch attempt, allowed, refused, or thrown (#186).
    /// </summary>
    /// <remarks>
    /// This is what makes "safer than handing someone a database credential" a property rather than an
    /// assertion: a scoped, attributable, single-topic call that leaves a record - including when the
    /// dispatch itself throws, which is exactly the scenario a "leaves a record" claim has to survive.
    /// It deliberately carries no payload and no response body — an audit trail that copies the data is
    /// a second copy of the thing being protected.
    /// </remarks>
    /// <param name="exceptionType">
    /// The thrown exception's type name, for the <c>outcome="dispatch-failed"</c> record (#186). Null
    /// for every other outcome.
    /// </param>
    private void Audit(string outcome, string? service, string? topic, string? exceptionType = null)
        => _logger?.LogInformation(
            "benzene.mesh.dispatch.audit outcome={outcome} email={email} service={service} topic={topic} environment={environment} exceptionType={exceptionType}",
            outcome, _identity.Email ?? "(unattributed)", service ?? "(none)", topic ?? "(none)",
            _identity.Environment ?? "(unstated)", exceptionType ?? "(none)");

    /// <inheritdoc />
    public async Task<IBenzeneResult<RawStringMessage>> HandleAsync(MeshDispatchRequest request)
    {
        if (!_gate.IsAllowed)
        {
            Audit("gate-blocked", request?.Service, request?.Topic);
            return BenzeneResult.SetFailed<RawStringMessage>(BenzeneResultStatus.Forbidden, _gate.BlockedReason);
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Service) || string.IsNullOrWhiteSpace(request.Topic))
        {
            Audit("bad-request", request?.Service, request?.Topic);
            return BenzeneResult.BadRequest<RawStringMessage>("A dispatch request needs both a 'service' and a 'topic'.");
        }

        // #187a: resolve the target BEFORE charging the per-target rate limit. Charging first let an
        // arbitrary, nonexistent service name pin a permanent rate-limit window (the key is the raw
        // caller-supplied string) - a not-found response now costs nothing to the limiter's map.
        var entry = _registry.Services.FirstOrDefault(s => string.Equals(s.Name, request.Service, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            Audit("not-found", request.Service, request.Topic);
            return BenzeneResult.NotFound<RawStringMessage>($"No service named '{request.Service}' is registered in the mesh.");
        }

        // THE PER-TARGET BOUND, applied here rather than in the HTTP guard because the target service
        // is inside the body and that layer deliberately does not parse it. Ten people each dispatching
        // politely still add up at one service, and the service is what this protects.
        if (!_limiter.TryAcquire($"target:{request.Service}", _guardOptions.MaxPerMinutePerTarget, out var retryAfter))
        {
            Audit("rate-limited", request.Service, request.Topic);
            return BenzeneResult.SetFailed<RawStringMessage>(BenzeneResultStatus.TooManyRequests,
                $"This mesh limits dispatches to '{request.Service}' to {_guardOptions.MaxPerMinutePerTarget} a minute, "
                + $"across everyone. Try again in {retryAfter}s.");
        }

        var dispatcher = _dispatchers.FirstOrDefault(d => string.Equals(d.Key, entry.Source, StringComparison.OrdinalIgnoreCase));
        if (dispatcher == null)
        {
            // #255 - this is the routine post-deploy misconfiguration (service registered, but the
            // matching transport dispatcher was never wired into the container), not a hostile input
            // like the branches above - so it must leave the same audit trail every other exit path
            // does, under its own outcome label, rather than vanishing silently from the trail.
            Audit("no-dispatcher", request.Service, request.Topic);
            return BenzeneResult.SetFailed<RawStringMessage>(BenzeneResultStatus.NotImplemented,
                $"No dispatcher is registered for source '{entry.Source}' (service '{entry.Name}'). "
                + "Register the matching transport dispatcher (e.g. AddMeshLambdaDispatcher() for AwsLambdaInvoke).");
        }

        var envelope = new MeshDispatchEnvelope(
            request.Topic!,
            request.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            request.Body ?? string.Empty);

        // #185: pass the ambient cancellation token (e.g. a UseTimeout() deadline) rather than
        // hardcoding None, so a stuck dispatch to an unresponsive service is actually interruptible.
        var token = _cancellation?.CancellationToken ?? CancellationToken.None;

        MeshDispatchResult result;
        try
        {
            result = await dispatcher.DispatchAsync(entry, envelope, token);
        }
        catch (Exception ex)
        {
            // #186: every other exit path audits before returning; a thrown dispatch must too, or the
            // "leaves a record" claim is false for exactly the case where a record matters most. The
            // exception is NOT swallowed - propagation semantics are unchanged, this only adds a log line.
            Audit("dispatch-failed", request.Service, request.Topic, ex.GetType().Name);
            throw;
        }

        Audit("dispatched", request.Service, request.Topic);
        var json = JsonSerializer.Serialize(result, ResultJsonOptions);
        return BenzeneResult.Ok(new RawStringMessage(json));
    }
}
