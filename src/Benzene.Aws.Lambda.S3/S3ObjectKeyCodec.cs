using System.Net;

namespace Benzene.Aws.Lambda.S3;

/// <summary>
/// Decodes the URL-encoded object key S3 puts on an event notification record
/// (<c>record.s3.object.key</c>), so handlers see the real key rather than its wire encoding.
/// </summary>
/// <remarks>
/// S3 event notifications URL-encode the object key (spaces become <c>+</c>, and other reserved/
/// non-ASCII bytes are percent-encoded), matching the encoding S3's own URLs use. Left undecoded, a
/// key containing a space, <c>+</c>, <c>&amp;</c>, <c>%</c>, or non-ASCII character reaches the
/// handler in its raw encoded form, so calling <c>GetObjectAsync</c> with it returns
/// <c>NoSuchKey</c>. This must use <see cref="WebUtility.UrlDecode(string)"/> rather than
/// <see cref="System.Uri.UnescapeDataString(string)"/> — the latter does not decode <c>+</c> to a
/// space, which is exactly the encoding S3 uses for spaces in keys.
/// </remarks>
public static class S3ObjectKeyCodec
{
    /// <summary>
    /// URL-decodes an S3 object key. Returns <c>null</c> unchanged.
    /// </summary>
    /// <param name="rawKey">The raw, URL-encoded key as it appears on the event notification record.</param>
    /// <returns>The decoded key, or <c>null</c> if <paramref name="rawKey"/> was <c>null</c>.</returns>
    public static string? Decode(string? rawKey)
    {
        return rawKey == null ? null : WebUtility.UrlDecode(rawKey);
    }
}
