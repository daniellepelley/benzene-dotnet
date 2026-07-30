using System;
using System.Threading.Tasks;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Aws.Lambda.Sqs;
using Benzene.Aws.Lambda.Sqs.TestHelpers;
using Benzene.Core;
using Benzene.Core.Exceptions;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.Sqs;

/// <summary>
/// An infrastructure failure fails the whole invocation; a business failure still fails one record.
/// </summary>
/// <remarks>
/// <para>
/// These are opposite problems that used to get identical handling. A handler throwing on one payload
/// is that message's fault and belongs in the DLQ. A missing registration is not the message's fault,
/// affects every message equally, and cannot be fixed by redelivering anything — reported per record
/// it drains the entire queue into the DLQ one message at a time while the function keeps returning
/// 200 and the service reports itself healthy.
/// </para>
/// <para>
/// The classification is deliberately narrow: only a proven resolution failure counts, because a
/// false positive fails a whole batch over one bad message. Anything unrecognised stays business.
/// </para>
/// </remarks>
public class SqsInfrastructureFailureTest
{
    [Fact]
    public void AResolutionFailureIsInfrastructure_EvenNestedInTheChain()
    {
        var nested = new Exception("outer", new BenzeneResolutionException("could not resolve IThing"));

        Assert.True(BenzeneFailure.IsInfrastructure(nested));
        Assert.True(BenzeneFailure.IsInfrastructure(new BenzeneResolutionException("x")));
    }

    [Fact]
    public void EverythingElseIsBusiness()
    {
        // The safe default. A handler that throws, a downstream that times out, a payload that will
        // not deserialize — all of them are one message's problem and keep per-record handling.
        Assert.False(BenzeneFailure.IsInfrastructure(new InvalidOperationException("the order was already shipped")));
        Assert.False(BenzeneFailure.IsInfrastructure(new BenzeneException("some other Benzene problem")));
        Assert.False(BenzeneFailure.IsInfrastructure(new TimeoutException()));
        Assert.False(BenzeneFailure.IsInfrastructure(null));
    }

    [Fact]
    public async Task AHandlerDependencyMissing_FailsTheWholeInvocation_RatherThanDeadLetteringOneRecord()
    {
        // The residual case after the start-up checks. A missing *middleware* dependency is now caught
        // at start-up by pipeline-resolution, so it never reaches here. A missing *handler* dependency
        // still can: the middleware constructs fine, and the handler is only built when a message is
        // dispatched to its topic — so this failure genuinely lives on the message path.
        //
        // Before this, it came back as one batch item failure and a 200: SQS redrove that message to
        // the DLQ and the next one did the same, forever, while the function looked healthy.
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.UsingBenzene(x => x
                    .AddBenzene()
                    .AddMessageHandlers(new[] { typeof(NeedsAnUnregisteredService) })
                    .AddSqs());
            })
            .Configure(app => app.UseSqs(sqs => sqs.UseMessageHandlers(new[] { typeof(NeedsAnUnregisteredService) })))
            .BuildHost();

        var sqsEvent = MessageBuilder.Create("needs:service", Defaults.MessageAsObject);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => host.SendSqsAsync(sqsEvent));

        Assert.True(BenzeneFailure.IsInfrastructure(exception),
            $"the invocation should fail with an infrastructure failure, got {exception.GetType().Name}");
    }

    public interface IUnregisteredService { }

    [Message("needs:service")]
    public class NeedsAnUnregisteredService : Benzene.Abstractions.MessageHandlers.IMessageHandler<ExampleRequestPayload, ExampleResponsePayload>
    {
        public NeedsAnUnregisteredService(IUnregisteredService service) { }

        public Task<Benzene.Abstractions.Results.IBenzeneResult<ExampleResponsePayload>> HandleAsync(ExampleRequestPayload message) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task AHandlerThatThrows_StillReportsJustThatRecord()
    {
        // The other half, and the one that must not regress: a business failure keeps partial-batch
        // reporting, so one poison message does not take the batch with it.
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.UsingBenzene(x => x
                    .AddBenzene()
                    .AddMessageHandlers(new[] { typeof(ThrowingHandler) })
                    .AddSqs());
            })
            .Configure(app => app.UseSqs(sqs => sqs.UseMessageHandlers(new[] { typeof(ThrowingHandler) })))
            .BuildHost();

        var response = await host.SendSqsAsync(
            MessageBuilder.Create("throwing:topic", Defaults.MessageAsObject));

        Assert.Single(response.BatchItemFailures);
    }

    [Message("throwing:topic")]
    public class ThrowingHandler : Benzene.Abstractions.MessageHandlers.IMessageHandler<ExampleRequestPayload, ExampleResponsePayload>
    {
        public Task<Benzene.Abstractions.Results.IBenzeneResult<ExampleResponsePayload>> HandleAsync(ExampleRequestPayload message) =>
            throw new InvalidOperationException("this one message is bad");
    }
}
