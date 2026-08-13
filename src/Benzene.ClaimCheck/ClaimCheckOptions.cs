using Benzene.Abstractions.Serialization;

namespace Benzene.ClaimCheck;

/// <summary>
/// Configuration for the claim-check offload and hydrate middleware.
/// </summary>
public class ClaimCheckOptions
{
    /// <summary>
    /// The default offload threshold: 192 KiB (196,608 UTF-8 bytes) of serialized body. Derived from
    /// the smallest common transport limit in the 256 KB family (SQS/SNS/EventBridge), leaving
    /// headroom for message attributes/envelope, which count against the same limit.
    /// </summary>
    public const int DefaultThresholdBytes = 192 * 1024;

    /// <summary>
    /// The serialized-body size, in UTF-8 bytes, at or above which an outbound message is offloaded
    /// to the <see cref="IClaimCheckStore"/> instead of sent inline. Defaults to
    /// <see cref="DefaultThresholdBytes"/>.
    /// </summary>
    public int ThresholdBytes { get; set; } = DefaultThresholdBytes;

    /// <summary>
    /// When <c>true</c>, every outbound message on this route is offloaded regardless of size.
    /// Defaults to <c>false</c>. Routes are already per-topic
    /// (<c>routing.Route("payments:capture", ...)</c>), so "always offload this topic" is just
    /// <c>UseClaimCheck(o =&gt; o.AlwaysOffload = true)</c> on that route - no separate topic map is
    /// needed.
    /// </summary>
    public bool AlwaysOffload { get; set; }

    /// <summary>
    /// The header the claim-check reference travels on. Defaults to
    /// <see cref="ClaimCheckHeaders.ClaimCheck"/>. Overriding it is a deployment agreement: the
    /// sending route's <c>UseClaimCheck(...)</c> and the receiving pipeline's
    /// <c>UseClaimCheck&lt;TContext&gt;(...)</c> must agree on the same name, or the receiver will
    /// never see the reference and will pass the (unusable) placeholder body straight to the handler.
    /// </summary>
    public string HeaderName { get; set; } = ClaimCheckHeaders.ClaimCheck;

    /// <summary>
    /// The serializer used to measure and store the outbound body. <c>null</c> (the default) uses
    /// <see cref="Benzene.Clients.JsonSerializer"/> - the same default every outbound transport
    /// context converter uses.
    /// </summary>
    /// <remarks>
    /// <strong>Serializer-consistency caveat</strong> (state it honestly - it is a real coupling, not
    /// a hypothetical one): this must match the serializer the route's own transport converter uses.
    /// Both default to <see cref="Benzene.Clients.JsonSerializer"/>; a route that passes a custom
    /// <see cref="ISerializer"/> to its converter (e.g. <c>UseSqs(url, mySerializer)</c>) must pass
    /// the same instance here (<c>UseClaimCheck(o =&gt; o.Serializer = mySerializer)</c>) - otherwise
    /// the bytes measured and stored are not what the converter would actually have produced for the
    /// un-offloaded message. Hoisting serialization out of the converters so this coupling disappears
    /// is a real future refactor, out of scope for this middleware.
    /// </remarks>
    public ISerializer? Serializer { get; set; }
}
