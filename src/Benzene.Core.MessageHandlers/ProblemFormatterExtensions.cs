using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers.Response;
using Benzene.Results;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// Registers an <see cref="IProblemFormatter{TContext}"/>, which writes failure bodies for one
/// transport context in the application's own error format.
/// </summary>
public static class ProblemFormatterExtensions
{
    /// <summary>
    /// Writes <typeparamref name="TContext"/>'s failure bodies with <paramref name="format"/>, which
    /// receives the problem document Benzene built (with <c>status</c> filled in on HTTP) and returns
    /// the body: a <see cref="string"/> as it is, anything else serialized by its runtime type.
    /// </summary>
    /// <remarks>
    /// Register it for the HTTP-native contexts only (<c>ApiGatewayContext</c>,
    /// <c>ApiGatewayV2Context</c>, <c>AspNetContext</c>). The envelope endpoint
    /// (<c>BenzeneMessageContext</c>) should keep the RFC 9457 document the spec requires
    /// (wire-contracts §1.3), which Benzene clients and the mesh read. Call it before the transport's
    /// own registration or after; either way it applies.
    /// </remarks>
    /// <typeparam name="TContext">The transport context type to format failures for.</typeparam>
    /// <param name="services">The Benzene service container.</param>
    /// <param name="format">Builds the body from the problem document and the failed result.</param>
    /// <param name="contentType">The body's content type; <c>application/json</c> by default.</param>
    /// <param name="applies">Which failures to format; <c>null</c> formats every failure.</param>
    /// <returns>The service container, for chaining.</returns>
    public static IBenzeneServiceContainer AddProblemFormatter<TContext>(this IBenzeneServiceContainer services,
        Func<ProblemDetails, IBenzeneResult, object> format, string contentType = "application/json",
        Func<IBenzeneResult, bool>? applies = null)
    {
        var formatter = new DelegateProblemFormatter<TContext>(format, contentType, applies);
        services.AddSingleton<IProblemFormatter<TContext>>(_ => formatter);
        return services;
    }

    /// <summary>
    /// Writes <typeparamref name="TContext"/>'s failure bodies with <typeparamref name="TFormatter"/>,
    /// resolved per message. See <see cref="AddProblemFormatter{TContext}(IBenzeneServiceContainer, Func{ProblemDetails, IBenzeneResult, object}, string, Func{IBenzeneResult, bool})"/>
    /// for where to register it.
    /// </summary>
    /// <typeparam name="TContext">The transport context type to format failures for.</typeparam>
    /// <typeparam name="TFormatter">The formatter.</typeparam>
    /// <param name="services">The Benzene service container.</param>
    /// <returns>The service container, for chaining.</returns>
    public static IBenzeneServiceContainer AddProblemFormatter<TContext, TFormatter>(this IBenzeneServiceContainer services)
        where TFormatter : class, IProblemFormatter<TContext>
    {
        services.AddScoped<IProblemFormatter<TContext>, TFormatter>();
        return services;
    }
}
