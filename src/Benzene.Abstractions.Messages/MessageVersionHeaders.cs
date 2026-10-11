namespace Benzene.Abstractions.Messages;

/// <summary>
/// The default name of the header/attribute that carries a message's version on the wire
/// (wire-contracts.md §2): <c>benzene-version</c>, the one signal that selects the handler and names the
/// payload's schema version.
/// <para>
/// There is one name, not a list: an unrelated <c>version</c> or <c>x-version</c> header a producer sends is
/// an ordinary header and selects nothing. The name is configurable in one place,
/// <see cref="IBenzeneWireNames.Version"/>, which every inbound version getter reads; HTTP additionally
/// accepts a <c>/v{version}</c> route segment, supplied exactly as if this header had been sent.
/// </para>
/// </summary>
public static class MessageVersionHeaders
{
    /// <summary>The canonical version header/attribute name: <c>benzene-version</c>
    /// (<see cref="BenzeneWireNames.DefaultVersion"/>).</summary>
    public const string Default = BenzeneWireNames.DefaultVersion;
}
