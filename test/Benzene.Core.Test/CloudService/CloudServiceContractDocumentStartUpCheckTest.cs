using System;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Aws.Lambda.ApiGateway;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Aws.Lambda.ApiGateway.TestHelpers;
using Benzene.Testing;
using Microsoft.Extensions.DependencyInjection;
using Benzene.CloudService;
using Benzene.Core.MessageHandlers;
using Benzene.Results;
using Benzene.Schema.OpenApi.Derivation;
using Benzene.Test.Examples;
using Xunit;

namespace Benzene.Test.CloudService
{
    /// <summary>
    /// <c>UseBenzeneCloudService</c> registers the contract-document start-up check by default, so a
    /// component-name collision (contract-document.md §4.1 rule 4) fails the host before the first
    /// message; <c>WithoutContractDocumentStartUpCheck()</c> opts out (e.g. for a Lambda cold start), and
    /// the same collision then surfaces on the first <c>benzene:spec</c> request instead.
    /// </summary>
    public class CloudServiceContractDocumentStartUpCheckTest
    {
        // The colliding handlers deliberately carry no [Message] attribute: the test assembly's handlers
        // are discovered by scanning, and a collision visible to every other test would break them all.
        // They are registered here, for these hosts only, as container definitions.
        private static IAwsLambdaEntryPoint Build(bool colliding, Action<ICloudServiceBuilder> configure)
        {
            return new InlineAwsLambdaStartUp()
                .ConfigureServices(services =>
                {
                    services.ConfigureServiceCollection();
                    if (colliding)
                    {
                        services.AddSingleton<IMessageHandlerDefinition>(_ => MessageHandlerDefinition.CreateInstance(
                            "collision:orders", string.Empty, typeof(StartUpCheckCollision.Orders.Item),
                            typeof(Benzene.Abstractions.Results.Void), typeof(StartUpCheckCollision.OrdersItemHandler)));
                        services.AddSingleton<IMessageHandlerDefinition>(_ => MessageHandlerDefinition.CreateInstance(
                            "collision:payments", string.Empty, typeof(StartUpCheckCollision.Payments.Item),
                            typeof(Benzene.Abstractions.Results.Void), typeof(StartUpCheckCollision.PaymentsItemHandler)));
                    }
                })
                .Configure(app => app
                    .UseApiGateway(apiGateway => apiGateway
                        .UseBenzeneCloudService("orders", configure)))
                .Build();
        }

        [Fact]
        public void ByDefault_AComponentNameCollisionFailsTheHostBeforeTheFirstMessage()
        {
            var failure = Assert.ThrowsAny<Exception>(() => Build(colliding: true, _ => { }));

            Assert.True(Unwrap<ComponentNameCollisionException>(failure) != null,
                $"start-up failed, but not with a component-name collision: {failure}");
        }

        [Fact]
        public async Task WithoutContractDocumentStartUpCheck_TheHostBuilds_AndTheCollisionSurfacesOnTheSpecRequest()
        {
            var entryPoint = Build(colliding: true, cloud => cloud.WithoutContractDocumentStartUpCheck());
            var host = new AwsLambdaBenzeneTestHost(entryPoint);

            var response = await host.SendApiGatewayAsync(HttpBuilder.Create("GET", CloudServicePaths.Spec, new { }));

            Assert.NotEqual(200, response.StatusCode);
        }

        [Fact]
        public void ByDefault_AWellFormedServiceBuilds()
        {
            Assert.NotNull(Build(colliding: false, _ => { }));
        }

        private static T? Unwrap<T>(Exception? exception) where T : Exception
        {
            while (exception != null)
            {
                if (exception is T match)
                {
                    return match;
                }

                if (exception is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        var found = Unwrap<T>(inner);
                        if (found != null)
                        {
                            return found;
                        }
                    }
                }

                exception = exception.InnerException;
            }

            return null;
        }
    }
}

namespace Benzene.Test.CloudService.StartUpCheckCollision
{
    namespace Orders
    {
        public class Item
        {
            public string? Sku { get; set; }
        }
    }

    namespace Payments
    {
        public class Item
        {
            public decimal Amount { get; set; }
        }
    }

    public class OrdersItemHandler : IMessageHandler<Orders.Item, Benzene.Abstractions.Results.Void>
    {
        public Task<IBenzeneResult<Benzene.Abstractions.Results.Void>> HandleAsync(Orders.Item request) =>
            Task.FromResult(BenzeneResult.Ok(new Benzene.Abstractions.Results.Void()));
    }

    public class PaymentsItemHandler : IMessageHandler<Payments.Item, Benzene.Abstractions.Results.Void>
    {
        public Task<IBenzeneResult<Benzene.Abstractions.Results.Void>> HandleAsync(Payments.Item request) =>
            Task.FromResult(BenzeneResult.Ok(new Benzene.Abstractions.Results.Void()));
    }
}
