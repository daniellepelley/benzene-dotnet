using System;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.AspNetCoreServer;
using Amazon.Lambda.Core;
using Benzene.Aws.Lambda.HttpBridge;

namespace Benzene.Aws.Lambda.AspNet;

/// <summary>
/// The built-in <see cref="IAwsHttpBridge{TRequest, TResponse}"/> that hands API Gateway REST API
/// (payload format 1.0) invocations to the hosted ASP.NET Core application.
/// </summary>
/// <remarks>
/// The REST (v1) sibling of <see cref="BenzeneAspNetBridge"/> — resolved by <c>UseHttpBridge()</c>'s
/// no-arg form. Same mechanism: it derives from <see cref="APIGatewayProxyFunction"/> purely to be
/// <em>called</em>, sharing the application's <see cref="IServiceProvider"/> and dispatching through the
/// <c>BenzeneLambdaServer</c> that captured the ASP.NET pipeline.
/// </remarks>
public class BenzeneAspNetRestBridge : APIGatewayProxyFunction,
    IAwsHttpBridge<APIGatewayProxyRequest, APIGatewayProxyResponse>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BenzeneAspNetRestBridge"/> class over the
    /// application's service provider.
    /// </summary>
    /// <param name="serviceProvider">The application's service provider, shared with ASP.NET.</param>
    public BenzeneAspNetRestBridge(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }

    /// <inheritdoc />
    Task<APIGatewayProxyResponse> IAwsHttpBridge<APIGatewayProxyRequest, APIGatewayProxyResponse>.HandleAsync(
        APIGatewayProxyRequest request, ILambdaContext context)
        => FunctionHandlerAsync(request, context);
}
