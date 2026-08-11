namespace Benzene.Abstractions;

/// <summary>
/// The single definition of the default correlation-id header key, matching the example in
/// <c>docs/specification/wire-contracts.md</c> §1.1 (<c>"x-correlation-id"</c>). Both directions read
/// this instead of hardcoding their own literal, so a fresh install joins up by default: the outbound
/// stamping middleware (<c>CorrelationIdMiddleware</c>) and the inbound diagnostics tag reader
/// (<c>ActivityMiddlewareDecorator</c>) previously defaulted to two different keys
/// (<c>"correlationId"</c> vs <c>"x-correlation-id"</c>), so a service's own outbound correlation id
/// never showed up on its inbound trace tag unless a caller happened to configure both consistently.
/// </summary>
public static class CorrelationHeaderDefaults
{
    /// <summary>The default correlation-id header key: <c>x-correlation-id</c>.</summary>
    public const string HeaderKey = "x-correlation-id";
}

/// <summary>
/// DI-registrable override for the correlation-id header key - register one instance and every
/// framework component that reads/writes the correlation header (currently
/// <c>CorrelationIdMiddleware</c> via <c>UseCorrelationId</c>, and <c>ActivityMiddlewareDecorator</c>'s
/// inbound trace tag) picks it up, instead of having to pass the same key to each independently.
/// Not registered by default; a component falls back to <see cref="CorrelationHeaderDefaults.HeaderKey"/>
/// when nothing is registered.
/// </summary>
public class CorrelationHeaderOptions
{
    /// <summary>The header key to use. Defaults to <see cref="CorrelationHeaderDefaults.HeaderKey"/>.</summary>
    public string HeaderKey { get; init; } = CorrelationHeaderDefaults.HeaderKey;
}
