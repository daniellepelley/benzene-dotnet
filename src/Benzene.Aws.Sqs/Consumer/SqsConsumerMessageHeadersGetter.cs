using System;
using System.Collections.Generic;
using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Aws.Sqs.Consumer;

/// <summary>
/// Extracts headers from an SQS message's string-typed message attributes.
/// </summary>
public class SqsConsumerMessageHeadersGetter : IMessageHeadersGetter<SqsConsumerMessageContext>
{
    /// <summary>
    /// Gets the string-typed message attributes as headers.
    /// </summary>
    /// <param name="context">The SQS consumer message context to extract headers from.</param>
    /// <returns>A dictionary of header names to values, limited to attributes with a <c>String</c> data type.</returns>
    public IDictionary<string, string> GetHeaders(SqsConsumerMessageContext context)
    {
        // OrdinalIgnoreCase on both branches - previously only the null-attributes fallback used it,
        // so the comparer (and therefore header-name case-sensitivity) silently depended on whether
        // the message happened to carry any attributes (#165).
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Null-guard the attribute map (a message deserialized from a payload with no attributes can
        // yield null), matching the SNS getter's hardening rather than NRE-ing out of the invocation.
        if (context.Message.MessageAttributes != null)
        {
            foreach (var attribute in context.Message.MessageAttributes)
            {
                if (attribute.Value.DataType == "String")
                {
                    headers[attribute.Key] = attribute.Value.StringValue;
                }
            }
        }

        return headers;
    }
}
