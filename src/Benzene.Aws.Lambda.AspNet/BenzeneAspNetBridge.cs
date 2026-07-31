using System;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.AspNetCoreServer;
using Amazon.Lambda.Core;
using Benzene.Aws.Lambda.HttpBridge;

namespace Benzene.Aws.Lambda.AspNet;

/// <summary>
/// The built-in <see cref="IAwsHttpBridge{TRequest, TResponse}"/> that hands API Gateway HTTP API
/// (payload format 2.0) invocations to the hosted ASP.NET Core application.
/// </summary>
/// <remarks>
/// <para>
/// This is the adapter <c>Benzene.Aws.Lambda.HttpBridge</c> documents as "a handful of lines in the
/// application" — supplied here so a mixed ASP.NET + Benzene function needs none of its own. It derives
/// from <see cref="APIGatewayHttpApiV2ProxyFunction"/> purely to be <em>called</em>: the function's
/// <c>FunctionHandlerAsync</c> is <c>public virtual</c> and its <c>ctor(IServiceProvider)</c> is
/// <c>protected</c>, so it dispatches into ASP.NET without having to be the Lambda entry point itself.
/// </para>
/// <para>
/// It shares the application's <see cref="IServiceProvider"/>, so ASP.NET and Benzene resolve the same
/// singletons; the <c>IServer</c> it dispatches through is the <c>BenzeneLambdaServer</c> that
/// captured the ASP.NET pipeline in <c>StartAsync</c>.
/// </para>
/// </remarks>
public class BenzeneAspNetBridge : APIGatewayHttpApiV2ProxyFunction,
    IAwsHttpBridge<APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BenzeneAspNetBridge"/> class over the application's
    /// service provider.
    /// </summary>
    /// <param name="serviceProvider">The application's service provider, shared with ASP.NET.</param>
    public BenzeneAspNetBridge(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }

    /// <inheritdoc />
    Task<APIGatewayHttpApiV2ProxyResponse> IAwsHttpBridge<APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse>.HandleAsync(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        => FunctionHandlerAsync(request, context);
}
