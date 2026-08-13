using System.Threading.Tasks;
using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Aws.Lambda.Sqs;

/// <summary>
/// Replaces the raw body string of an SQS message. The Lambda event POCO is mutable, so hydrating
/// a claim-checked body is a plain assignment - see <c>Benzene.ClaimCheck.ClaimCheckHydrateMiddleware{TContext}</c>,
/// which resolves this via <see cref="IMessageBodySetter{TContext}"/> to replace the placeholder body
/// before deserialization.
/// </summary>
public class SqsMessageBodySetter : IMessageBodySetter<SqsMessageContext>
{
    /// <summary>
    /// Sets the raw body on the SQS message.
    /// </summary>
    /// <param name="context">The SQS message context to set the body on.</param>
    /// <param name="body">The hydrated body to replace the message's raw body with.</param>
    public Task SetBody(SqsMessageContext context, string body)
    {
        context.SqsMessage.Body = body;
        return Task.CompletedTask;
    }
}
