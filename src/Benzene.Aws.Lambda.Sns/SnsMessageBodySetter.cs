using System.Threading.Tasks;
using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Aws.Lambda.Sns;

/// <summary>
/// Replaces the raw message body of an SNS record. The Lambda event POCO is mutable, so hydrating
/// a claim-checked body is a plain assignment - see <c>Benzene.ClaimCheck.ClaimCheckHydrateMiddleware{TContext}</c>,
/// which resolves this via <see cref="IMessageBodySetter{TContext}"/> to replace the placeholder body
/// before deserialization.
/// </summary>
public class SnsMessageBodySetter : IMessageBodySetter<SnsRecordContext>
{
    /// <summary>
    /// Sets the raw message body on the SNS record.
    /// </summary>
    /// <param name="context">The SNS record context to set the body on.</param>
    /// <param name="body">The hydrated body to replace the record's raw message with.</param>
    public Task SetBody(SnsRecordContext context, string body)
    {
        context.SnsRecord.Sns.Message = body;
        return Task.CompletedTask;
    }
}
