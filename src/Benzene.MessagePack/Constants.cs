namespace Benzene.MessagePack;

/// <summary>
/// Shared constants for MessagePack content negotiation.
/// </summary>
public static class Constants
{
    /// <summary>The <c>content-type</c>/<c>accept</c> header name used for negotiation.</summary>
    public const string ContentTypeHeader = "content-type";

    /// <summary>The MessagePack media type, <c>application/msgpack</c>.</summary>
    public const string MessagePackContentType = "application/msgpack";
}
