namespace Benzene.Examples.AwsMesh.Shared;

/// <summary>
/// Demo-only helper for the claim-check dogfood (<c>work/archive/claim-check-plan-2026-08.md</c> Phase 6; README
/// "Claim-check: oversized payloads"). Orders' <c>payments:capture</c> send is claim-checked
/// (<c>OutboundSend.ClaimChecked</c>), and the demo needs a genuinely oversized payload to prove the
/// offload actually triggers — but <c>CapturePayment</c> is a GENERATED contract type
/// (<c>contracts/payments.spec.json</c> → the topic-client codegen), and this dogfood deliberately does
/// not grow that contract just to inflate a demo payload (see the README's "Contract note": claim-check
/// is pure outbound middleware and needs no contract change to work at all, which is the point of doing
/// it as middleware rather than a client-generation concern).
/// </summary>
/// <remarks>
/// So instead of a new field, an optional demo "supporting document" attachment rides inside
/// <c>CapturePayment.OrderId</c> — a plain string field whose schema
/// (<c>{"type": "string", "description": "Not Empty"}</c>) is honoured exactly as-is either way,
/// verbatim, with no relaxation of validation on either side (<c>CapturePaymentValidator</c> only checks
/// <c>NotEmpty</c>). <see cref="Embed"/> folds it on at send time (Orders); <see cref="Strip"/> takes it
/// back off on receive (Payments), so the attachment never propagates past the claim-checked hop —
/// <c>shipping:book</c> and <c>payment:captured</c> stay small, which matters because neither of those
/// downstream routes is claim-checked (EventBridge/SQS's own ~256&#160;KB transport limit still applies to
/// them).
/// </remarks>
public static class ClaimCheckDemoPayload
{
    private const string Delimiter = "|doc:";

    /// <summary>
    /// Folds <paramref name="supportingDocument"/> onto <paramref name="orderId"/> for the wire.
    /// Returns <paramref name="orderId"/> unchanged when <paramref name="supportingDocument"/> is
    /// <see langword="null"/> or empty — the ordinary, small-payload path.
    /// </summary>
    public static string Embed(string orderId, string? supportingDocument)
        => string.IsNullOrEmpty(supportingDocument) ? orderId : $"{orderId}{Delimiter}{supportingDocument}";

    /// <summary>
    /// Splits a wire order id back into the real order id, discarding any attached document. Returns
    /// <paramref name="wireOrderId"/> unchanged when it carries no attachment.
    /// </summary>
    public static string Strip(string wireOrderId)
    {
        var index = wireOrderId.IndexOf(Delimiter, StringComparison.Ordinal);
        return index < 0 ? wireOrderId : wireOrderId[..index];
    }
}
