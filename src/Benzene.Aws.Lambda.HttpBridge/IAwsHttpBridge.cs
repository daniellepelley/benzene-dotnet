using System.Threading.Tasks;
using Amazon.Lambda.Core;

namespace Benzene.Aws.Lambda.HttpBridge;

/// <summary>
/// Hands an HTTP-shaped Lambda event to an HTTP application Benzene does not own — most commonly an
/// ASP.NET Core application hosted by <c>Amazon.Lambda.AspNetCoreServer</c>.
/// </summary>
/// <typeparam name="TRequest">The API Gateway/ALB request payload type.</typeparam>
/// <typeparam name="TResponse">The matching response payload type.</typeparam>
/// <remarks>
/// <para>
/// This is a <em>port</em>, not an integration. Benzene owns the claiming — recognising an HTTP
/// payload among the SQS, SNS and EventBridge events arriving at the same function — and hands the
/// request over. Whoever implements this interface owns everything after that.
/// </para>
/// <para>
/// The point of the indirection is dependency weight. An ASP.NET integration package would pull the
/// whole hosting stack into anything referencing it, and would need re-releasing whenever that stack
/// moved. This package references the event POCOs and nothing else; the adapter is a handful of
/// lines in the application, which already has ASP.NET on hand:
/// </para>
/// <code>
/// public class AspNetBridge : APIGatewayHttpApiV2ProxyFunction,
///                             IAwsHttpBridge&lt;APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse&gt;
/// {
///     public AspNetBridge(IServiceProvider services) : base(services) { }
///
///     Task&lt;APIGatewayHttpApiV2ProxyResponse&gt; IAwsHttpBridge&lt;…&gt;.HandleAsync(
///         APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
///         =&gt; FunctionHandlerAsync(request, context);
/// }
/// </code>
/// <para>
/// Nothing about the contract is ASP.NET-specific — an application can bridge to any HTTP stack, or
/// to a hand-written handler, by implementing it.
/// </para>
/// <para>
/// <b>Exception handling is part of "owns everything after that".</b> An exception this method lets
/// escape is not caught and turned into an HTTP error response by anything in this package — it
/// propagates out of the Lambda function invocation as a raw Lambda function error, which API Gateway
/// reports to the caller as an opaque 502, not the caller's own error shape. This differs from
/// Benzene's own HTTP binding (<c>Benzene.Aws.Lambda.ApiGateway</c>'s <c>UseApiGateway</c>), which
/// catches a handler exception inside the pipeline and produces an in-band HTTP error response. An
/// implementation that wants API Gateway-shaped error responses (a JSON body, a chosen status code)
/// must catch and map its own exceptions before returning from <see cref="HandleAsync(TRequest, ILambdaContext)"/> — exactly as
/// an ASP.NET Core application would with its own exception-handling middleware.
/// </para>
/// </remarks>
public interface IAwsHttpBridge<TRequest, TResponse>
{
    /// <summary>
    /// Handles one HTTP-shaped invocation.
    /// </summary>
    /// <param name="request">The API Gateway/ALB request payload.</param>
    /// <param name="context">The Lambda execution context for this invocation.</param>
    /// <returns>The response payload to return to the caller.</returns>
    Task<TResponse> HandleAsync(TRequest request, ILambdaContext context);
}
