using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Messages.BenzeneMessage;

namespace Benzene.Clients.InProcess;

/// <summary>
/// The built, name-keyed set of in-process dispatchers <see cref="InProcessMessagingBuilder"/>
/// produces - one <see cref="IMiddlewareApplication{TIn,TOut}"/> per module registered via
/// <c>AddInProcessMessaging(registry => registry.Add(name, configure))</c>. Registered as a single
/// singleton instance; <c>.UseInProcess(name)</c> resolves the named dispatcher from it at dispatch
/// time.
/// </summary>
public class InProcessDispatcherRegistry
{
    private readonly IReadOnlyDictionary<string, IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse>> _dispatchersByName;

    /// <summary>
    /// Initializes a new instance of the <see cref="InProcessDispatcherRegistry"/> class.
    /// </summary>
    /// <param name="dispatchersByName">Every built pipeline's dispatcher, keyed by the name it was added under.</param>
    public InProcessDispatcherRegistry(
        IReadOnlyDictionary<string, IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse>> dispatchersByName)
    {
        _dispatchersByName = dispatchersByName;
    }

    /// <summary>Every registered pipeline name, for diagnostics (the start-up check, exception messages).</summary>
    public IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)_dispatchersByName.Keys;

    /// <summary>
    /// Resolves the dispatcher registered under <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The pipeline name, as passed to both <c>Add(name, ...)</c> and <c>.UseInProcess(name)</c>.</param>
    /// <returns>The named dispatcher.</returns>
    /// <exception cref="InProcessPipelineNotFoundException">No pipeline is registered under <paramref name="name"/>.</exception>
    public IMiddlewareApplication<IBenzeneMessageRequest, IBenzeneMessageResponse> Resolve(string name)
    {
        if (_dispatchersByName.TryGetValue(name, out var dispatcher))
        {
            return dispatcher;
        }

        throw new InProcessPipelineNotFoundException(name, Names);
    }
}
