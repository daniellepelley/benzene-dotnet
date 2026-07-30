using System;
using System.Threading.Tasks;
using Amazon.Lambda.ApplicationLoadBalancerEvents;
using Amazon.Lambda.AspNetCoreServer;
using Amazon.Lambda.Core;
using Benzene.Aws.Lambda.HttpBridge;

namespace Benzene.Aws.Lambda.AspNet;

/// <summary>
/// The built-in <see cref="IAwsHttpBridge{TRequest, TResponse}"/> that hands Application Load Balancer
/// invocations to the hosted ASP.NET Core application.
/// </summary>
/// <remarks>
/// <para>
/// The ALB sibling of <see cref="BenzeneAspNetBridge"/> — resolved by <c>UseHttpBridgeAlb()</c>'s no-arg
/// form. Same mechanism: it derives from <see cref="ApplicationLoadBalancerFunction"/> purely to be
/// <em>called</em>, sharing the application's <see cref="IServiceProvider"/> and dispatching through the
/// <c>BenzeneLambdaServer</c> that captured the ASP.NET pipeline.
/// </para>
/// <para>
/// ALB is a distinct payload pair, not a flavour of API Gateway: its response requires
/// <c>statusDescription</c>, which the API Gateway response type has no field for. If one function is
/// fronted by both an ALB and a REST API, register <c>UseHttpBridgeAlb()</c> before <c>UseHttpBridge()</c>
/// in the event pipeline — see the <c>Benzene.Aws.Lambda.HttpBridge</c> docs for why.
/// </para>
/// </remarks>
public class BenzeneAspNetAlbBridge : ApplicationLoadBalancerFunction,
    IAwsHttpBridge<ApplicationLoadBalancerRequest, ApplicationLoadBalancerResponse>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BenzeneAspNetAlbBridge"/> class over the
    /// application's service provider.
    /// </summary>
    /// <param name="serviceProvider">The application's service provider, shared with ASP.NET.</param>
    public BenzeneAspNetAlbBridge(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }

    /// <inheritdoc />
    Task<ApplicationLoadBalancerResponse> IAwsHttpBridge<ApplicationLoadBalancerRequest, ApplicationLoadBalancerResponse>.HandleAsync(
        ApplicationLoadBalancerRequest request, ILambdaContext context)
        => FunctionHandlerAsync(request, context);
}
