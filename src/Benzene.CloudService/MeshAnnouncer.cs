using System.Text;
using System.Text.Json;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Results;
using Benzene.HealthChecks;
using Benzene.HealthChecks.Core;
using Benzene.Mesh.Wire;

namespace Benzene.CloudService;

/// <summary>
/// The outbound announcement half of the mesh feeds (mesh.md §2, §4–§5): registers the service's
/// contract document with the collector (retrying until the collector is up), then heartbeats on an
/// interval, carrying the aggregated health check response, the service version and the registered
/// document's <c>registrationHash</c>.
///
/// The §6 degradation rule applies throughout: every failure - collector down, send faulted,
/// document derivation or health check throwing - is swallowed and retried later, and nothing here
/// ever runs on, blocks, or fails an invocation. Started at wire-up when a reduced document is
/// available then (explicit handler types), otherwise by the first invocation; either way the first
/// invocation derives the complete document - the one <c>benzene:spec</c> serves - and the announcer
/// re-registers when it differs from what it registered (see <see cref="EnsureStarted"/>).
/// </summary>
internal sealed class MeshAnnouncer : IAsyncDisposable, IDisposable
{
    private static readonly HttpClient DefaultHttp = new();

    private readonly object _gate = new();
    private readonly MeshServiceInfo _info;
    private readonly CloudServiceDocumentSource _documentSource;
    private readonly string _collectorEnvelopeUrl;
    private readonly IHealthCheck[] _healthChecks;
    private readonly HttpClient _http;
    private readonly TimeSpan _heartbeatInterval;
    private readonly CancellationTokenSource _stopping = new();
    // Wakes the loop early when a new document is offered, so a re-registration doesn't wait out a
    // heartbeat interval.
    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _complete;
    private Task? _runTask;
    // The document the collector last accepted, and the one waiting to replace it (null when none).
    private volatile RegistrationDocument? _registered;
    private volatile RegistrationDocument? _pending;

    public MeshAnnouncer(
        MeshServiceInfo info,
        CloudServiceDocumentSource documentSource,
        string collectorEnvelopeUrl,
        IEnumerable<IHealthCheck> healthChecks,
        HttpClient? http = null,
        TimeSpan? heartbeatInterval = null)
    {
        _info = info;
        _documentSource = documentSource;
        _collectorEnvelopeUrl = collectorEnvelopeUrl;
        _healthChecks = healthChecks.ToArray();
        _http = http ?? DefaultHttp;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Offers this service's document to the announce loop, starting the loop on the first offer; safe
    /// (and, once the complete document is in hand, free) to call from every invocation. With a
    /// <paramref name="resolver"/> the complete document is derived from the container; without one
    /// (wire-up) only the reduced wire-up document, if any, is available. A derivation that throws is
    /// swallowed and retried on the next call (#47).
    /// </summary>
    public void EnsureStarted(IServiceResolver? resolver)
    {
        if (Volatile.Read(ref _complete) == 1)
        {
            return;
        }

        RegistrationDocument? document;
        try
        {
            document = resolver == null ? _documentSource.TryGetWireUp() : _documentSource.Get(resolver);
        }
        catch
        {
            // Deriving the document threw (e.g. the registry isn't ready yet) - swallow it and let the
            // next invocation retry, rather than failing the invocation that triggered it (#47).
            return;
        }

        if (document == null)
        {
            return; // nothing to announce yet - the next invocation derives it
        }

        if (document.Complete)
        {
            Interlocked.Exchange(ref _complete, 1);
        }

        lock (_gate)
        {
            if (_registered?.Json == document.Json || _pending?.Json == document.Json)
            {
                return; // already registered, or about to be
            }

            _pending = document;
            if (_runTask == null)
            {
                _runTask = Task.Run(() => RunAsync(_stopping.Token));
                return;
            }

            try
            {
                if (_wake.CurrentCount == 0)
                {
                    _wake.Release(); // the loop is between heartbeats: register now
                }
            }
            catch (ObjectDisposedException)
            {
                // disposed while an invocation offered a document - nothing left to announce to
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pending = _pending;
                if (pending != null)
                {
                    if (!await SendAsync(MeshTopics.Register, pending.Json))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                        continue;
                    }

                    lock (_gate)
                    {
                        _registered = pending;
                        if (ReferenceEquals(_pending, pending))
                        {
                            _pending = null;
                        }
                    }
                }

                var registered = _registered;
                if (registered != null)
                {
                    try
                    {
                        await SendAsync(MeshTopics.Heartbeat, MeshJson.Serialize(new MeshHeartbeat
                        {
                            Service = _info.Service,
                            ServiceVersion = _info.ServiceVersion,
                            InstanceId = _info.InstanceId,
                            RegistrationHash = registered.RegistrationHash,
                            SentAt = DateTimeOffset.UtcNow,
                            Health = await GetHealthAsync()
                        }));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // a heartbeat that cannot be built or sent is lost; the loop - and the next
                        // heartbeat - must survive it (spec §6)
                    }
                }

                await _wake.WaitAsync(_heartbeatInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping - nothing to clean up; a collector notices via missing heartbeats (spec §6)
        }
    }

    private async Task<HealthCheckResponse> GetHealthAsync()
    {
        try
        {
            var result = await HealthCheckProcessor.PerformHealthChecksAsync(
                HealthChecks.Constants.DefaultHealthCheckTopic, _healthChecks);
            return result is IBenzeneResult<HealthCheckResponse> typed
                ? typed.Payload ?? new HealthCheckResponse(result.IsSuccessful, new Dictionary<string, HealthCheckResult>())
                : new HealthCheckResponse(result.IsSuccessful, new Dictionary<string, HealthCheckResult>());
        }
        catch
        {
            // a broken check must degrade the heartbeat's payload, never stop the heartbeats
            return new HealthCheckResponse(false, new Dictionary<string, HealthCheckResult>());
        }
    }

    private async Task<bool> SendAsync(string topic, string body)
    {
        try
        {
            var envelope = JsonSerializer.Serialize(
                new { topic, headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), body });
            using var content = new StringContent(envelope, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_collectorEnvelopeUrl, content, _stopping.Token);
            if (!response.IsSuccessStatusCode)
            {
                return false; // the carrier failed: no envelope was processed
            }

            // The collector answers 200 for every envelope it processed (transport-bindings §1), so a
            // rejected registration is a 200 whose envelope says isSuccessful: false - read it, or the
            // retry loop would stop on a registration the collector refused.
            var responseBody = await response.Content.ReadAsStringAsync(_stopping.Token);
            return EnvelopeSucceeded(responseBody);
        }
        catch
        {
            return false; // collector down: reduced mesh, never a service failure (spec §6)
        }
    }

    // The response envelope's isSuccessful (wire-contracts §1.2) - absent means a malformed envelope,
    // which is unsuccessful. A body that is not an envelope at all leaves the HTTP success standing.
    private static bool EnvelopeSucceeded(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("statusCode", out _))
            {
                return true;
            }

            return root.TryGetProperty("isSuccessful", out var isSuccessful) && isSuccessful.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        // Await the announce loop before disposing the CTS: the loop reads _stopping.Token on its
        // in-flight SendAsync, so disposing the source out from under it would throw
        // ObjectDisposedException (swallowed, but a real use-after-dispose). Mirrors
        // HttpMeshTraceExporter, which stores and awaits its pump task the same way.
        Task? runTask;
        lock (_gate)
        {
            runTask = _runTask;
        }
        if (runTask != null)
        {
            try
            {
                await runTask;
            }
            catch
            {
                // best-effort shutdown of side-channel telemetry (spec §6) - never throw from dispose
            }
        }

        _stopping.Dispose();
        _wake.Dispose();
    }

    /// <summary>
    /// Synchronous disposal bridge. A DI container disposed synchronously (MS DI's
    /// <c>ServiceProvider.Dispose()</c> throws on a service that only implements
    /// <see cref="IAsyncDisposable"/>) needs this path. It runs <see cref="DisposeAsync"/> but only
    /// waits a bounded time so an unreachable collector can't hang shutdown - the announce loop is
    /// side-channel telemetry (spec §6), the cancellation is already signalled, so the loop winds
    /// down on its own even if we stop waiting.
    /// </summary>
    public void Dispose()
    {
        try
        {
            DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // DisposeAsync is already best-effort and never throws for a down collector.
        }
    }
}
