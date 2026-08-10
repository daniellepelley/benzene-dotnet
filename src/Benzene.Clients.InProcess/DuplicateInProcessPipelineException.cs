namespace Benzene.Clients.InProcess;

/// <summary>
/// Thrown by <see cref="InProcessMessagingBuilder"/>'s internal build step when the same pipeline
/// name was registered via <c>Add(name, configure)</c> more than once within a single
/// <c>AddInProcessMessaging(...)</c> call - the in-process counterpart of
/// <c>DuplicateOutboundRouteException</c>.
/// </summary>
public class DuplicateInProcessPipelineException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicateInProcessPipelineException"/> class.
    /// </summary>
    /// <param name="name">The pipeline name that was registered more than once.</param>
    public DuplicateInProcessPipelineException(string name)
        : base($"In-process pipeline '{name}' was registered more than once - each name may only " +
               "be added once per AddInProcessMessaging(...) call.")
    {
        Name = name;
    }

    /// <summary>Gets the pipeline name that was registered more than once.</summary>
    public string Name { get; }
}
