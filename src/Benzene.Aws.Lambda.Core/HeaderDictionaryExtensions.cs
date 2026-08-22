using System.Collections.Generic;

namespace Benzene.Aws.Lambda.Core;

/// <summary>
/// Small helpers shared by the AWS Lambda trigger packages' <c>IMessageHeadersGetter</c>
/// implementations (e.g. <c>Benzene.Aws.Lambda.EventBridge.EventBridgeMessageHeadersGetter</c>,
/// <c>Benzene.Aws.Lambda.DynamoDb.DynamoDbMessageHeadersGetter</c>).
/// </summary>
public static class HeaderDictionaryExtensions
{
    /// <summary>
    /// Adds <paramref name="key"/>/<paramref name="value"/> to <paramref name="headers"/> only if
    /// <paramref name="value"/> is non-null and non-empty - so an envelope field the event source
    /// didn't populate is simply omitted rather than added as an empty header.
    /// </summary>
    /// <param name="headers">The header dictionary to add to.</param>
    /// <param name="key">The header name.</param>
    /// <param name="value">The header value, or <c>null</c>/empty to skip adding it.</param>
    public static void AddIfPresent(this IDictionary<string, string> headers, string key, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            headers[key] = value;
        }
    }
}
