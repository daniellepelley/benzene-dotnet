using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Benzene.Abstractions.Middleware;
using Benzene.Clients;
using Benzene.Clients.Aws.Sqs;
using Benzene.Microsoft.Dependencies;
using Benzene.Resilience;
using Benzene.Results;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Clients;

/// <summary>
/// A send that fails in transport is a result, not an exception, so retrying it is
/// <c>UseRetry(shouldRetryContext: OutboundRetry.*)</c> placed before the transport.
/// </summary>
public class OutboundRetryTest
{
    [Theory]
    [InlineData(BenzeneResultStatus.ServiceUnavailable, true)]
    [InlineData(BenzeneResultStatus.TooManyRequests, true)]
    [InlineData(BenzeneResultStatus.Timeout, false)]
    [InlineData(BenzeneResultStatus.ValidationError, false)]
    [InlineData(BenzeneResultStatus.UnexpectedError, false)]
    [InlineData(BenzeneResultStatus.Accepted, false)]
    public void Transient_RetriesOnlyTheFailuresThatLeftNothingApplied(string status, bool retried)
    {
        Assert.Equal(retried, OutboundRetry.Transient(ContextWith(status)));
    }

    [Theory]
    [InlineData(BenzeneResultStatus.ServiceUnavailable, true)]
    [InlineData(BenzeneResultStatus.Timeout, true)]
    [InlineData(BenzeneResultStatus.ValidationError, true)]
    [InlineData(BenzeneResultStatus.Ok, false)]
    [InlineData(BenzeneResultStatus.Accepted, false)]
    public void Unsuccessful_RetriesEveryFailure(string status, bool retried)
    {
        Assert.Equal(retried, OutboundRetry.Unsuccessful(ContextWith(status)));
    }

    [Theory]
    [InlineData(BenzeneResultStatus.ServiceUnavailable, true)]
    [InlineData(BenzeneResultStatus.Timeout, true)]
    [InlineData(BenzeneResultStatus.TooManyRequests, false)]
    public void OnStatus_RetriesTheNamedStatuses(string status, bool retried)
    {
        var predicate = OutboundRetry.OnStatus(BenzeneResultStatus.ServiceUnavailable, BenzeneResultStatus.Timeout);

        Assert.Equal(retried, predicate(ContextWith(status)));
    }

    [Fact]
    public void Predicates_NoResponse_DoNotRetry()
    {
        var context = new OutboundContext(Defaults.Topic, "x");

        Assert.False(OutboundRetry.Transient(context));
        Assert.False(OutboundRetry.Unsuccessful(context));
        Assert.False(OutboundRetry.OnStatus(BenzeneResultStatus.ServiceUnavailable)(context));
    }

    [Fact]
    public async Task UseRetryBeforeTheTransport_ASendThatFailsInTransport_IsRetriedAndSucceeds()
    {
        var sqs = SqsFailingTimes(1);
        var sender = SenderWith(sqs.Object, pipeline => pipeline
            .UseRetry(2, delay: _ => Task.CompletedTask, shouldRetryContext: OutboundRetry.Transient)
            .UseSqs(Defaults.SqsQueueUrl));

        var result = await sender.SendAsync<ExampleRequestPayload, Void>(Defaults.Topic, new ExampleRequestPayload());

        Assert.True(result.IsSuccessful);
        sqs.Verify(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task UseRetryBeforeTheTransport_ASendThatKeepsFailing_ReturnsTheFailureAfterTheLastAttempt()
    {
        var sqs = SqsFailingTimes(int.MaxValue);
        var sender = SenderWith(sqs.Object, pipeline => pipeline
            .UseRetry(2, delay: _ => Task.CompletedTask, shouldRetryContext: OutboundRetry.Transient)
            .UseSqs(Defaults.SqsQueueUrl));

        var result = await sender.SendAsync<ExampleRequestPayload, Void>(Defaults.Topic, new ExampleRequestPayload());

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        sqs.Verify(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task UseRetryAfterTheTransport_NeverRuns_BecauseTheTransportEndsThePipeline()
    {
        // The placement the docs used to show. The transport converter is terminal, so the retry
        // registered after it is never reached and the failed send is not retried.
        var sqs = SqsFailingTimes(1);
        var sender = SenderWith(sqs.Object, pipeline => pipeline
            .UseSqs(Defaults.SqsQueueUrl)
            .UseRetry(2, delay: _ => Task.CompletedTask, shouldRetryContext: OutboundRetry.Transient));

        var result = await sender.SendAsync<ExampleRequestPayload, Void>(Defaults.Topic, new ExampleRequestPayload());

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
        sqs.Verify(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static OutboundContext ContextWith(string status)
    {
        return new OutboundContext(Defaults.Topic, "x") { Response = BenzeneResult.Set<Void>(status, BenzeneResultStatus.IsSuccess(status)) };
    }

    private static Mock<IAmazonSQS> SqsFailingTimes(int failures)
    {
        var calls = 0;
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++calls <= failures
                ? throw new AmazonSQSException("Service Unavailable")
                : Task.FromResult(new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK }));
        return sqs;
    }

    private static IBenzeneMessageSender SenderWith(IAmazonSQS sqs, Action<IMiddlewarePipelineBuilder<OutboundContext>> route)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sqs);
        new MicrosoftBenzeneServiceContainer(services).AddOutboundRouting(routing => routing.Route(Defaults.Topic, route));
        return new MicrosoftServiceResolverAdapter(services.BuildServiceProvider()).GetService<IBenzeneMessageSender>();
    }
}
