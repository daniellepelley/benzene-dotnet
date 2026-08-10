namespace Benzene.Clients.InProcess;

/// <summary>
/// Thrown by <see cref="InProcessDispatcherRegistry.Resolve"/> when <c>.UseInProcess(name)</c>
/// names a pipeline nothing registered via <c>AddInProcessMessaging(...)</c>.
/// </summary>
/// <remarks>
/// This is the runtime backstop for a mistake <see cref="InProcessRouteStartUpCheck"/> already
/// catches at start-up (enforced by default - see <c>BenzeneStartUpCheckMode</c>). It only fires
/// when that check has been disabled or downgraded to advisory, or when
/// <see cref="Abstractions.DI.IServiceResolver.GetService{T}"/> resolves this registry from some
/// path the check doesn't see - either way, a typo'd or never-registered pipeline name should never
/// silently resolve to the wrong pipeline or hang, so <c>Resolve</c> throws immediately.
/// </remarks>
public class InProcessPipelineNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InProcessPipelineNotFoundException"/> class.
    /// </summary>
    /// <param name="name">The pipeline name that was requested but never registered.</param>
    /// <param name="registeredNames">Every pipeline name that <em>is</em> registered, for the error message.</param>
    public InProcessPipelineNotFoundException(string name, IEnumerable<string> registeredNames)
        : base(BuildMessage(name, registeredNames))
    {
        Name = name;
    }

    /// <summary>Gets the pipeline name that was requested but never registered.</summary>
    public string Name { get; }

    private static string BuildMessage(string name, IEnumerable<string> registeredNames)
    {
        var registered = registeredNames.ToArray();
        var known = registered.Length == 0
            ? "none - AddInProcessMessaging(...) was never called"
            : string.Join(", ", registered.Select(n => $"'{n}'"));
        return $"No in-process pipeline named '{name}' is registered. Registered pipeline names: {known}. " +
               $"Add it via AddInProcessMessaging(registry => registry.Add(\"{name}\", ...)).";
    }
}
