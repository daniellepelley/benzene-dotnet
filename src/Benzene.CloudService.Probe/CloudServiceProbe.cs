using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Benzene.CloudService.Probe;

/// <summary>
/// Runs an external, black-box conformance probe against a live Benzene Cloud Service
/// (docs/specification/cloud-service-profile.md), asserting the observable surfaces of the profile's
/// R1–R5 and R7 from outside - health response shape, envelope round-trip, the derived spec at its
/// HTTP path and on the reserved <c>benzene:spec</c> topic over the envelope, default paths - over real HTTP, without
/// relying on anything the service says about itself. The meshed-service claim (mesh.md §7.1: M1
/// feeds, M2 trace propagation) is reported apart, as far as it is observable - which, from a single
/// service, it is not.
///
/// This is intentionally decoupled from <c>Benzene.CloudService</c> (see
/// <see cref="CloudServiceProbePaths"/>'s remarks): it works against any service that speaks the
/// wire contracts, .NET or not.
/// </summary>
public static class CloudServiceProbe
{
    private const string HealthDescription = "Health checks";
    private const string InvokeDescription = "Wire-envelope invocability";
    private const string SpecDescription = "Derived spec: the benzene:spec topic wherever the transport carries a reply, and the HTTP spec surface";
    private const string FeedsDescription = "Service-side mesh feeds (registration, heartbeats, traces)";
    private const string PropagationDescription = "Trace context join and propagation";

    private const string HealthcheckEnvelope = "{\"topic\":\"benzene:healthcheck\",\"headers\":{},\"body\":\"{}\"}";
    private const string SpecEnvelope = "{\"topic\":\"benzene:spec\",\"headers\":{},\"body\":\"{}\"}";

    /// <summary>
    /// Probes the service at <paramref name="httpClient"/>'s <c>BaseAddress</c> and returns a
    /// tri-state assessment of the profile's R1–R5 and R7, plus the meshed-service claim's M1 and M2
    /// reported separately. Never throws for an unreachable or non-conformant service -
    /// unreachability and shape mismatches are reported as <see cref="CloudServiceProbeVerdict.NotSatisfied"/>
    /// or <see cref="CloudServiceProbeVerdict.Inconclusive"/> verdicts, not exceptions.
    /// </summary>
    /// <param name="httpClient">
    /// The client to probe with; its <c>BaseAddress</c> is the target service. The caller owns its
    /// lifetime (timeout, base address, any auth headers needed to reach the service).
    /// </param>
    /// <param name="options">Path overrides and the M2 bonus-signal toggle; defaults match the profile's /benzene/* standard.</param>
    /// <param name="cancellationToken">Cancels the run; an externally-requested cancellation propagates, an internal per-request timeout does not.</param>
    public static async Task<CloudServiceProbeReport> RunAsync(
        HttpClient httpClient,
        CloudServiceProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (httpClient is null)
        {
            throw new ArgumentNullException(nameof(httpClient));
        }

        options ??= new CloudServiceProbeOptions();

        var health = await ProbeHealthAsync(httpClient, options, cancellationToken).ConfigureAwait(false);
        var invoke = await ProbeInvokeAsync(httpClient, options, cancellationToken).ConfigureAwait(false);
        var specPath = await ProbeSpecPathAsync(httpClient, options, cancellationToken).ConfigureAwait(false);
        var specTopic = await ProbeSpecTopicAsync(httpClient, options, cancellationToken).ConfigureAwait(false);
        var spec = EvaluateR5(specPath, specTopic);

        var requirements = new List<CloudServiceProbeRequirement>
        {
            EvaluateR1(health.Reached || invoke.Reached || specPath.Reached || specTopic.Reached),
            EvaluateR2(specTopic),
            health.Requirement,
            invoke.Requirement,
            spec,
            EvaluateR7(options, health.Requirement, invoke.Requirement, spec)
        };

        var meshRequirements = new List<CloudServiceProbeRequirement>
        {
            EvaluateM1(),
            EvaluateM2(options, invoke.Requirement, specTopic.Satisfied)
        };

        return new CloudServiceProbeReport(requirements, meshRequirements);
    }

    // ---- R3: GET the health path; satisfied iff 200 OR 503 with a boolean isHealthy field (wire-contracts.md §5). ----
    private static async Task<(CloudServiceProbeRequirement Requirement, bool Reached)> ProbeHealthAsync(
        HttpClient client, CloudServiceProbeOptions options, CancellationToken ct)
    {
        var (reached, status, body, failure) = await GetAsync(client, options.HealthPath, ct).ConfigureAwait(false);
        if (!reached)
        {
            return (new CloudServiceProbeRequirement("R3", HealthDescription, CloudServiceProbeVerdict.NotSatisfied,
                $"GET {options.HealthPath} did not reach the service: {failure}"), false);
        }
        // A conformant service returns 200 when healthy and 503 when unhealthy - both carry the same
        // health-report body (transport-bindings.md §1: the health route maps the payload's isHealthy
        // onto the status line; the health query itself is ok either way). Runtime degradation is NOT a conformance
        // failure (cloud-service-profile.md §4), so accept either and judge R3 on the report shape -
        // matching how the mesh aggregator reads a 503 body rather than treating it as a fetch failure.
        if (status != 200 && status != 503)
        {
            return (new CloudServiceProbeRequirement("R3", HealthDescription, CloudServiceProbeVerdict.NotSatisfied,
                $"GET {options.HealthPath} returned {status}, expected 200 or 503"), true);
        }
        if (!TryGetBoolField(body, "isHealthy", out _))
        {
            return (new CloudServiceProbeRequirement("R3", HealthDescription, CloudServiceProbeVerdict.NotSatisfied,
                $"{status} response at {options.HealthPath} did not have a boolean 'isHealthy' field (wire-contracts.md §5)"), true);
        }
        return (new CloudServiceProbeRequirement("R3", HealthDescription, CloudServiceProbeVerdict.Satisfied,
            $"GET {options.HealthPath} returned {status} with a boolean 'isHealthy' field"), true);
    }

    // ---- R4: POST a synthetic envelope; satisfied iff 200 with the {statusCode, headers, body} shape (wire-contracts.md §1.2). ----
    private static async Task<(CloudServiceProbeRequirement Requirement, bool Reached)> ProbeInvokeAsync(
        HttpClient client, CloudServiceProbeOptions options, CancellationToken ct)
    {
        var (reached, status, body, failure) = await PostAsync(
            client, options.InvokePath, HealthcheckEnvelope, options.SendTraceParentProbe, ct).ConfigureAwait(false);
        if (!reached)
        {
            return (new CloudServiceProbeRequirement("R4", InvokeDescription, CloudServiceProbeVerdict.NotSatisfied,
                $"POST {options.InvokePath} did not reach the service: {failure}"), false);
        }
        if (status != 200)
        {
            return (new CloudServiceProbeRequirement("R4", InvokeDescription, CloudServiceProbeVerdict.NotSatisfied,
                $"POST {options.InvokePath} with a synthetic envelope returned {status}, expected 200"), true);
        }
        if (!TryParseEnvelopeResponse(body, out _, out var shapeReason))
        {
            return (new CloudServiceProbeRequirement("R4", InvokeDescription, CloudServiceProbeVerdict.NotSatisfied,
                $"200 response at {options.InvokePath} did not have the {{statusCode, headers, body}} envelope shape (wire-contracts.md §1.2): {shapeReason}"), true);
        }
        return (new CloudServiceProbeRequirement("R4", InvokeDescription, CloudServiceProbeVerdict.Satisfied,
            $"POST {options.InvokePath} with a synthetic envelope round-tripped a valid {{statusCode, headers, body}} response"), true);
    }

    // ---- R5, HTTP half: GET the spec path; satisfied iff 200 with a non-empty JSON object. No specific spec format is assumed. ----
    private static async Task<(bool Satisfied, bool Reached, string Reason)> ProbeSpecPathAsync(
        HttpClient client, CloudServiceProbeOptions options, CancellationToken ct)
    {
        var (reached, status, body, failure) = await GetAsync(client, options.SpecPath, ct).ConfigureAwait(false);
        if (!reached)
        {
            return (false, false, $"GET {options.SpecPath} did not reach the service: {failure}");
        }
        if (status != 200)
        {
            return (false, true, $"GET {options.SpecPath} returned {status}, expected 200");
        }
        if (!TryParseNonEmptyObject(body))
        {
            return (false, true,
                $"200 response at {options.SpecPath} did not parse as a non-empty JSON object (the HTTP surface does not mandate a specific spec format - OpenAPI, AsyncAPI, or Benzene's own - only that a real derived document is there)");
        }
        return (true, true, $"GET {options.SpecPath} returned 200 with a non-empty JSON object body");
    }

    // ---- R5, topic half: POST topic "benzene:spec" over the envelope; satisfied iff the 200 envelope wraps a contract document with a non-empty "service" (contract-document.md §1, §7). ----
    private static async Task<(bool Satisfied, bool Reached, string Reason, JsonArray? Topics)> ProbeSpecTopicAsync(
        HttpClient client, CloudServiceProbeOptions options, CancellationToken ct)
    {
        var (reached, status, body, failure) = await PostAsync(
            client, options.InvokePath, SpecEnvelope, options.SendTraceParentProbe, ct).ConfigureAwait(false);
        if (!reached)
        {
            return (false, false, $"POST {options.InvokePath} with topic 'benzene:spec' did not reach the service: {failure}", null);
        }
        if (status != 200)
        {
            return (false, true,
                $"POST {options.InvokePath} with topic 'benzene:spec' returned {status}, expected 200 (the reserved spec topic does not appear to be served over the envelope)", null);
        }
        if (!TryParseEnvelopeResponse(body, out var innerBody, out var envelopeReason))
        {
            return (false, true,
                $"200 response to the 'benzene:spec' topic did not have the {{statusCode, headers, body}} envelope shape (wire-contracts.md §1.2): {envelopeReason}", null);
        }
        if (!TryParseContractDocument(innerBody, out var document, out var documentReason))
        {
            return (false, true,
                $"200 response to the 'benzene:spec' topic did not parse as a contract document with a non-empty 'service' field (contract-document.md §1): {documentReason}", null);
        }

        return (true, true,
            $"the reserved 'benzene:spec' topic, posted to {options.InvokePath}, served the contract document for service '{document!["service"]}'",
            document["requests"] as JsonArray);
    }

    // ---- R5: both halves - the HTTP spec surface and the benzene:spec topic over the envelope. ----
    private static CloudServiceProbeRequirement EvaluateR5(
        (bool Satisfied, bool Reached, string Reason) specPath,
        (bool Satisfied, bool Reached, string Reason, JsonArray? Topics) specTopic)
    {
        const string transportCaveat =
            "R5 requires benzene:spec wherever the transport carries a reply; an HTTP probe observes the HTTP " +
            "surfaces only, so another reply-carrying transport's answer is not verified here (a one-way binding " +
            "cannot answer and is never counted against R5)";
        var verdict = specPath.Satisfied && specTopic.Satisfied
            ? CloudServiceProbeVerdict.Satisfied
            : CloudServiceProbeVerdict.NotSatisfied;
        return new CloudServiceProbeRequirement("R5", SpecDescription, verdict,
            $"HTTP surface: {specPath.Reason}. Topic: {specTopic.Reason}. ({transportCaveat}.)");
    }

    // ---- R1: inferential - satisfied iff anything responded at all, since that proves a hosted pipeline exists. ----
    private static CloudServiceProbeRequirement EvaluateR1(bool anyReached)
    {
        const string description = "Hosted middleware pipeline";
        return anyReached
            ? new CloudServiceProbeRequirement("R1", description, CloudServiceProbeVerdict.Satisfied,
                "at least one probed surface responded over HTTP, which proves a hosted pipeline exists behind a transport binding - an in-process-only pipeline would have nothing for this probe to reach")
            : new CloudServiceProbeRequirement("R1", description, CloudServiceProbeVerdict.NotSatisfied,
                "none of the health, invoke, spec, or benzene:spec surfaces responded at all; no hosted pipeline was reachable at the configured base address");
    }

    // ---- R2: inferential from the contract document's requests, read over the benzene:spec topic (R5's topic half) - GET /benzene/spec is only checked for a non-empty object. ----
    private static CloudServiceProbeRequirement EvaluateR2(
        (bool Satisfied, bool Reached, string Reason, JsonArray? Topics) specTopic)
    {
        const string description = "Message handlers via the registry";
        if (!specTopic.Satisfied)
        {
            return new CloudServiceProbeRequirement("R2", description, CloudServiceProbeVerdict.Inconclusive,
                "no contract document reachable over the benzene:spec topic to read registered topics from - absence of evidence isn't evidence of absence");
        }
        if (specTopic.Topics is { Count: > 0 })
        {
            return new CloudServiceProbeRequirement("R2", description, CloudServiceProbeVerdict.Satisfied,
                $"the contract document lists {specTopic.Topics.Count} served topic(s) in 'requests' (contract-document.md §2), which can only be populated from the handler registry");
        }
        return new CloudServiceProbeRequirement("R2", description, CloudServiceProbeVerdict.Inconclusive,
            "the contract document is reachable but lists no requests; a service can legitimately have zero handlers registered so far - absence of evidence isn't evidence of absence");
    }

    // ---- R7: only assessable when the probe itself used the defaults; otherwise it can't tell what the service defaults to. ----
    private static CloudServiceProbeRequirement EvaluateR7(
        CloudServiceProbeOptions options,
        CloudServiceProbeRequirement health,
        CloudServiceProbeRequirement invoke,
        CloudServiceProbeRequirement spec)
    {
        const string description = "Default service standard paths";
        if (!options.UsesDefaultPaths)
        {
            return new CloudServiceProbeRequirement("R7", description, CloudServiceProbeVerdict.Inconclusive,
                "the probe was configured with non-default path(s), so it cannot tell whether the service itself defaults to /benzene/invoke, /benzene/spec, and /benzene/health - it was told to look elsewhere");
        }

        var failing = new List<string>();
        if (health.Verdict != CloudServiceProbeVerdict.Satisfied) failing.Add("R3 (health)");
        if (invoke.Verdict != CloudServiceProbeVerdict.Satisfied) failing.Add("R4 (invoke)");
        if (spec.Verdict != CloudServiceProbeVerdict.Satisfied) failing.Add("R5 (spec)");

        if (failing.Count == 0)
        {
            return new CloudServiceProbeRequirement("R7", description, CloudServiceProbeVerdict.Satisfied,
                "the probe used the spec's /benzene/* default paths and R3, R4 and R5 all checked out there");
        }
        return new CloudServiceProbeRequirement("R7", description, CloudServiceProbeVerdict.NotSatisfied,
            $"the probe used the spec's /benzene/* default paths, but {string.Join(", ", failing)} did not check out there");
    }

    // ---- M1 (meshed-service claim, mesh.md §7.1): never observable by a single-service black-box probe; always Inconclusive. ----
    private static CloudServiceProbeRequirement EvaluateM1()
    {
        return new CloudServiceProbeRequirement("M1", FeedsDescription, CloudServiceProbeVerdict.Inconclusive,
            "registration (benzene:mesh:register), heartbeats (benzene:mesh:heartbeat) and trace delivery " +
            "(benzene:mesh:traces) go from the service to a collector, so they cannot be observed by probing the " +
            "service alone - ask the collector whether this service has registered. Not observable from outside " +
            "the service; this never counts against the Cloud Service Profile");
    }

    // ---- M2 (meshed-service claim, mesh.md §7.1): never observable by a single-service black-box probe; always Inconclusive, with an optional labeled bonus signal. ----
    private static CloudServiceProbeRequirement EvaluateM2(
        CloudServiceProbeOptions options,
        CloudServiceProbeRequirement invoke,
        bool specTopicSatisfied)
    {
        const string baseReason =
            "trace context propagation - the inbound join, and traceparent forwarded on every outbound send and " +
            "published event - cannot be verified by a single-service black-box HTTP probe; proving it " +
            "requires either a second service to observe forwarded traceparent headers, or a mesh collector " +
            "observing a declared edge actually being exercised via trace parentage (mesh.md §3, §4.2 - " +
            "observed liveness only: the producer/consumer graph itself is declared by each registered " +
            "contract document's requests, events and sends, never derived from trace parentage)";

        if (!options.SendTraceParentProbe)
        {
            return new CloudServiceProbeRequirement("M2", PropagationDescription, CloudServiceProbeVerdict.Inconclusive, baseReason);
        }

        var nonBreaking = invoke.Verdict == CloudServiceProbeVerdict.Satisfied && specTopicSatisfied;
        var bonus = nonBreaking
            ? " (weak bonus signal: the service still responded correctly with a synthetic traceparent header attached to the envelope calls - R4's and R5's benzene:spec topic - this is non-breakage only, not proof of propagation, and does not upgrade this verdict)"
            : " (a bonus traceparent header was attached to the envelope calls - R4's and R5's benzene:spec topic - but at least one of them did not succeed, so even the weak non-breakage signal is absent)";

        return new CloudServiceProbeRequirement("M2", PropagationDescription, CloudServiceProbeVerdict.Inconclusive, baseReason + bonus);
    }

    private static async Task<(bool Reached, int? StatusCode, string? Body, string? FailureReason)> GetAsync(
        HttpClient client, string path, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(path, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (true, (int)response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, null, null, "request timed out");
        }
        catch (Exception ex)
        {
            return (false, null, null, ex.Message);
        }
    }

    private static async Task<(bool Reached, int? StatusCode, string? Body, string? FailureReason)> PostAsync(
        HttpClient client, string path, string jsonContent, bool includeTraceParent, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
            };
            if (includeTraceParent)
            {
                request.Headers.TryAddWithoutValidation("traceparent", GenerateSyntheticTraceParent());
            }
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (true, (int)response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, null, null, "request timed out");
        }
        catch (Exception ex)
        {
            return (false, null, null, ex.Message);
        }
    }

    /// <summary>A syntactically-valid, random W3C traceparent (https://www.w3.org/TR/trace-context/#traceparent-header) for the M2 bonus probe.</summary>
    private static string GenerateSyntheticTraceParent()
    {
        Span<byte> traceId = stackalloc byte[16];
        Span<byte> spanId = stackalloc byte[8];
        RandomNumberGenerator.Fill(traceId);
        RandomNumberGenerator.Fill(spanId);
        return $"00-{Convert.ToHexString(traceId).ToLowerInvariant()}-{Convert.ToHexString(spanId).ToLowerInvariant()}-01";
    }

    private static bool TryGetBoolField(string? raw, string fieldName, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return false;
        }
        if (node is not JsonObject obj || obj[fieldName] is not JsonValue field)
        {
            return false;
        }
        var kind = field.GetValueKind();
        if (kind != JsonValueKind.True && kind != JsonValueKind.False)
        {
            return false;
        }
        value = field.GetValue<bool>();
        return true;
    }

    private static bool TryParseNonEmptyObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return false;
        }
        return node is JsonObject { Count: > 0 };
    }

    /// <summary>Validates the {statusCode, headers, body} response envelope shape (wire-contracts.md §1.2) and extracts the pre-serialized body string.</summary>
    private static bool TryParseEnvelopeResponse(string? raw, out string? innerBody, out string? reason)
    {
        innerBody = null;
        reason = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            reason = "empty response body";
            return false;
        }
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            reason = $"not valid JSON: {ex.Message}";
            return false;
        }
        if (node is not JsonObject obj)
        {
            reason = "not a JSON object";
            return false;
        }
        if (obj["statusCode"] is not JsonValue sc || sc.GetValueKind() != JsonValueKind.String)
        {
            reason = "missing a string 'statusCode' field";
            return false;
        }
        if (obj["headers"] is not JsonObject)
        {
            reason = "missing an object 'headers' field";
            return false;
        }
        if (obj["body"] is not JsonValue bodyValue || bodyValue.GetValueKind() != JsonValueKind.String)
        {
            reason = "missing a string 'body' field";
            return false;
        }
        innerBody = bodyValue.GetValue<string>();
        return true;
    }

    /// <summary>Parses an envelope's inner body as a contract document (contract-document.md §1) and requires a non-empty "service" field.</summary>
    private static bool TryParseContractDocument(string? innerBody, out JsonObject? document, out string? reason)
    {
        document = null;
        reason = null;
        if (string.IsNullOrWhiteSpace(innerBody))
        {
            reason = "empty envelope body";
            return false;
        }
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(innerBody);
        }
        catch (JsonException ex)
        {
            reason = $"envelope body is not valid JSON: {ex.Message}";
            return false;
        }
        if (node is not JsonObject obj)
        {
            reason = "envelope body is not a JSON object";
            return false;
        }
        if (obj["service"] is not JsonValue sv || sv.GetValueKind() != JsonValueKind.String || string.IsNullOrEmpty(sv.GetValue<string>()))
        {
            reason = "missing a non-empty string 'service' field";
            return false;
        }
        document = obj;
        return true;
    }
}
