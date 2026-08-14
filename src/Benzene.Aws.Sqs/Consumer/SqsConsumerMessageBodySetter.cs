using System.Threading.Tasks;
using Benzene.Abstractions.Messages.Mappers;

namespace Benzene.Aws.Sqs.Consumer;

/// <summary>
/// Replaces the raw body string of an SQS message received by the polling consumer. The underlying
/// <see cref="Amazon.SQS.Model.Message"/> is mutable, so hydrating a claim-checked body is a plain
/// assignment - see <c>Benzene.ClaimCheck.ClaimCheckHydrateMiddleware{TContext}</c>, which resolves
/// this via <see cref="IMessageBodySetter{TContext}"/> to replace the placeholder body before
/// deserialization. Mirrors <c>Benzene.Aws.Lambda.Sqs.SqsMessageBodySetter</c>'s Lambda-side
/// equivalent for the standalone (non-Lambda) consumer.
/// </summary>
public class SqsConsumerMessageBodySetter : IMessageBodySetter<SqsConsumerMessageContext>
{
    /// <summary>
    /// Sets the raw body on the SQS message.
    /// </summary>
    /// <param name="context">The SQS consumer message context to set the body on.</param>
    /// <param name="body">The hydrated body to replace the message's raw body with.</param>
    public Task SetBody(SqsConsumerMessageContext context, string body)
    {
        context.Message.Body = body;
        return Task.CompletedTask;
    }
}
