using System.Net;

namespace Benzene.Aws.Lambda.S3;

/// <summary>
/// Encodes/decodes the URL-encoded object key S3 puts on an event notification record
/// (<c>record.s3.object.key</c>), so handlers see the real key rather than its wire encoding, and
/// so test helpers building a fake record can produce the same wire encoding S3 itself would.
/// </summary>
/// <remarks>
/// S3 event notifications URL-encode the object key (spaces become <c>+</c>, and other reserved/
/// non-ASCII bytes are percent-encoded), matching the encoding S3's own URLs use. Left undecoded, a
/// key containing a space, <c>+</c>, <c>&amp;</c>, <c>%</c>, or non-ASCII character reaches the
/// handler in its raw encoded form, so calling <c>GetObjectAsync</c> with it returns
/// <c>NoSuchKey</c>. This must use <see cref="WebUtility.UrlDecode(string)"/> /
/// <see cref="WebUtility.UrlEncode(string)"/> rather than <see cref="System.Uri.UnescapeDataString(string)"/> /
/// <see cref="System.Uri.EscapeDataString(string)"/> — the latter pair does not treat <c>+</c> as a
/// space, which is exactly the encoding S3 uses for spaces in keys. <see cref="Encode"/> and
/// <see cref="Decode"/> are true inverses of each other (both are documented by the BCL as mirrors of
/// one another), so a key built with <see cref="Encode"/> - e.g. by
/// <c>Benzene.Aws.Lambda.S3.TestHelpers</c>'s <c>AsS3</c> - decodes back to exactly what was passed
/// in when the real getter (<see cref="Decode"/>) reads it. Keep both directions in this one type so
/// the encode and decode sides can never drift apart from each other (see #191).
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

    /// <summary>
    /// URL-encodes an S3 object key using the same scheme S3 itself applies (space becomes <c>+</c>,
    /// other reserved/non-ASCII bytes are percent-encoded) - the exact inverse of <see cref="Decode"/>.
    /// Returns <c>null</c> unchanged.
    /// </summary>
    /// <param name="rawKey">The plain (decoded) key to encode as it would appear on the wire.</param>
    /// <returns>The URL-encoded key, or <c>null</c> if <paramref name="rawKey"/> was <c>null</c>.</returns>
    public static string? Encode(string? rawKey)
    {
        return rawKey == null ? null : WebUtility.UrlEncode(rawKey);
    }
}
