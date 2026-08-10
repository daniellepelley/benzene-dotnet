namespace Benzene.Clients.InProcess;

/// <summary>
/// Thrown by <see cref="InProcessRouteStartUpCheck"/> when one or more <c>.UseInProcess(name)</c>
/// routes name a pipeline nothing registered via <c>AddInProcessMessaging(...)</c> - a missing or
/// misspelled pipeline caught at start-up instead of the first time that route is actually sent to.
/// </summary>
public class MissingInProcessPipelineException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MissingInProcessPipelineException"/> class.
    /// </summary>
    /// <param name="missingNames">Every pipeline name referenced by a route with no matching registration.</param>
    /// <param name="registeredNames">Every pipeline name that <em>is</em> registered, for the error message.</param>
    public MissingInProcessPipelineException(string[] missingNames, IReadOnlyCollection<string> registeredNames)
        : base(BuildMessage(missingNames, registeredNames))
    {
        MissingNames = missingNames;
    }

    /// <summary>Gets every pipeline name referenced by a route with no matching registration.</summary>
    public string[] MissingNames { get; }

    private static string BuildMessage(string[] missingNames, IReadOnlyCollection<string> registeredNames)
    {
        var missing = string.Join(", ", missingNames.Select(n => $"'{n}'"));
        var known = registeredNames.Count == 0
            ? "none - AddInProcessMessaging(...) was never called"
            : string.Join(", ", registeredNames.Select(n => $"'{n}'"));
        return $"The following in-process pipeline name(s) are routed to via .UseInProcess(...) but " +
               $"never registered: {missing}. Registered pipeline names: {known}. Add each missing one " +
               "via AddInProcessMessaging(registry => registry.Add(\"name\", ...)).";
    }
}
