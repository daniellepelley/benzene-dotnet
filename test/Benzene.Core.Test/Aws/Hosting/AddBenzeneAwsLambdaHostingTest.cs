using System;
using System.Linq;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.AspNetCoreServer.Internal;
using Benzene.Aws.Lambda.AspNet;
using Benzene.Aws.Lambda.HttpBridge;
using Benzene.Aws.Lambda.Sqs;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Aws.Hosting;

/// <summary>
/// <c>AddBenzeneAwsLambdaHosting</c> is the whole wiring the ASP.NET + Benzene cookbook used to do by
/// hand. These assert the three things it guarantees: the built-in bridge is registered, <c>AddBenzene()</c>
/// is applied for you (the composition footgun), and the Benzene-driven <c>IServer</c> is taken over only
/// inside Lambda.
/// </summary>
public class AddBenzeneAwsLambdaHostingTest
{
    private static IServiceCollection Compose()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Deliberately WITHOUT .AddBenzene() — the helper must add it, or SQS records fail far from the
        // cause with 'Unable to resolve IDefaultStatuses'.
        services.UsingBenzene(x => x
            .AddMessageHandlers(typeof(Defaults).Assembly)
            .AddSqs());

        services.AddBenzeneAwsLambdaHosting(events => events
            .UseHttpBridgeV2()
            .UseSqs(sqs => sqs.UseMessageHandlers()));

        return services;
    }

    [Fact]
    public void RegistersTheBuiltInBridge()
    {
        var services = Compose();

        Assert.Contains(services, d => d.ServiceType
            == typeof(IAwsHttpBridge<APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse>));
    }

    [Fact]
    public void CallsAddBenzeneForYou()
    {
        using var provider = Compose().BuildServiceProvider();

        // Resolvable only because the helper called AddBenzene() — the user's UsingBenzene above did not.
        Assert.NotNull(provider.GetService<IDefaultStatuses>());
    }

    [Fact]
    public void TakesOverTheServer_OnlyInsideLambda()
    {
        WithLambdaRuntimeApi(null, () =>
            Assert.DoesNotContain(Compose(), d => d.ServiceType == typeof(IServer)));

        WithLambdaRuntimeApi("127.0.0.1:9001", () =>
        {
            using var provider = Compose().BuildServiceProvider();
            var server = provider.GetService<IServer>();
            Assert.NotNull(server);
            // The Benzene-driven server is a LambdaServer subclass — it captures the ASP.NET pipeline in
            // StartAsync before running the Benzene loop.
            Assert.IsAssignableFrom<LambdaServer>(server);
        });
    }

    private static void WithLambdaRuntimeApi(string value, Action action)
    {
        var original = Environment.GetEnvironmentVariable("AWS_LAMBDA_RUNTIME_API");
        Environment.SetEnvironmentVariable("AWS_LAMBDA_RUNTIME_API", value);
        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_LAMBDA_RUNTIME_API", original);
        }
    }
}
