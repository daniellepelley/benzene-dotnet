using System.Threading;

namespace Benzene.Azure.Function.Core;

/// <summary>
/// Represents a built Azure Function app that dispatches requests to the matching registered entry
/// point application, based on the request type.
/// </summary>
public interface IAzureFunctionApp
{
    /// <summary>
    /// Handles a request that expects a response, dispatching to the registered entry point application
    /// whose request/response types match.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="request">The request to handle.</param>
    /// <param name="name">The discriminator key to match (for multiple entry points of the same type), or <c>null</c>.</param>
    /// <param name="cancellationToken">
    /// The isolated worker's cancellation token for this invocation (bound as a trigger method
    /// parameter by the Functions host), or <see cref="CancellationToken.None"/> if the trigger
    /// doesn't request one. Forwarded so any component resolved during the pipeline can observe it
    /// via <c>ICancellationTokenAccessor</c> - see <c>work/cancellation-design.md</c>.
    /// </param>
    /// <returns>A task that resolves to the response produced by the matching entry point application.</returns>
    Task<TResponse> HandleAsync<TRequest, TResponse>(TRequest request, string? name = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles a fire-and-forget request, dispatching to the registered entry point application whose
    /// request type matches (and whose discriminator key equals <paramref name="name"/>, when given).
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="request">The request to handle.</param>
    /// <param name="name">The discriminator key to match (for multiple entry points of the same type), or <c>null</c>.</param>
    /// <param name="cancellationToken">
    /// The isolated worker's cancellation token for this invocation (bound as a trigger method
    /// parameter by the Functions host), or <see cref="CancellationToken.None"/> if the trigger
    /// doesn't request one. Forwarded so any component resolved during the pipeline can observe it
    /// via <c>ICancellationTokenAccessor</c> - see <c>work/cancellation-design.md</c>.
    /// </param>
    /// <returns>A task that completes when the matching entry point application has finished handling the request.</returns>
    Task HandleAsync<TRequest>(TRequest request, string? name = null, CancellationToken cancellationToken = default);
}
