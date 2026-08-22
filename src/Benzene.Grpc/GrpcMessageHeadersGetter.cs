using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Grpc;

/// <summary>Reads the inbound request metadata (excluding binary entries) as headers from a <see cref="GrpcContext"/>.</summary>
public class GrpcMessageHeadersGetter : IMessageHeadersGetter<GrpcContext>
{
    /// <inheritdoc />
    public IDictionary<string, string> GetHeaders(GrpcContext context)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in context.CallContext.RequestHeaders)
        {
            if (entry.IsBinary)
            {
                continue;
            }

            headers[entry.Key] = entry.Value;
        }

        return headers;
    }
}
