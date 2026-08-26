using System.Threading.Tasks;
using Amazon.Lambda.SQSEvents;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Aws.Lambda.Sqs;
using Benzene.Aws.Lambda.Sqs.TestHelpers;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Aws.Helpers;
using Benzene.Test.Examples;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Test.Aws;

// A user-registered override of a TryAdd*-registered framework default, resolved through
// ConfigureServices - see IMessageHandlerResultSetterTest below.
public class CustomSqsMessageHandlerResultSetter : IMessageHandlerResultSetter<SqsMessageContext>
{
    public static bool WasInvoked;

    public Task SetResultAsync(SqsMessageContext context, IMessageHandlerResult messageHandlerResult)
    {
        WasInvoked = true;
        context.MessageResult = messageHandlerResult.BenzeneResult;
        return Task.CompletedTask;
    }
}

// #106: InlineAwsLambdaStartUp.Build() must run ConfigureServices before Configure (matching
// AwsLambdaHost's production order) so a user's TryAdd*-registered ConfigureServices override of a
// framework default (here IMessageHandlerResultSetter<SqsMessageContext>, TryAdd-registered by
// UseSqs's AddSqs) wins - exactly as it does in production - instead of losing to the transport's own
// TryAdd because Configure ran first and claimed the registration.
public class InlineAwsLambdaStartUpOrderingTest
{
    [Fact]
    public async Task ConfigureServicesOverride_OfATryAddDefault_WinsUnderTheInlineHost()
    {
        CustomSqsMessageHandlerResultSetter.WasInvoked = false;
        var mockExampleService = new Mock<IExampleService>();

        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services => services
                .AddTransient<ILogger<MessageRouter<SqsMessageContext>>>(_ => NullLogger<MessageRouter<SqsMessageContext>>.Instance)
                .AddTransient<ILogger>(_ => NullLogger.Instance)
                .AddTransient(_ => mockExampleService.Object)
                .UsingBenzene(x => x
                    .AddBenzene()
                    // Registered via TryAdd, exactly like the framework default it overrides - first
                    // registration wins, so which of ConfigureServices/Configure runs first decides
                    // whether this or UseSqs's own default ends up installed.
                    .TryAddScoped<IMessageHandlerResultSetter<SqsMessageContext>, CustomSqsMessageHandlerResultSetter>()))
            .Configure(app => app
                .UseSqs(sqs => sqs.UseMessageHandlers()))
            .BuildHost();

        var request = MessageBuilder.Create(Defaults.Topic, Defaults.MessageAsObject).AsSqs();

        SQSBatchResponse batchResponse = await host.SendSqsAsync(request);

        Assert.True(CustomSqsMessageHandlerResultSetter.WasInvoked);
        Assert.Empty(batchResponse.BatchItemFailures);
    }
}
