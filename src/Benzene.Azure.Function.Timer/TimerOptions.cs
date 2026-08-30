namespace Benzene.Azure.Function.Timer;

/// <summary>
/// Configures how <see cref="TimerApplication"/> handles the tick's exceptions and failure results.
/// Mirrors every sibling Azure Function trigger package's <c>*Options</c> type (e.g. <c>EventGridOptions</c>),
/// applied here to Timer's single tick rather than a batch.
/// </summary>
public class TimerOptions
{
    /// <summary>
    /// Gets or sets whether an unhandled exception from the pipeline is caught (logged, and the
    /// invocation reports success) instead of left to cascade and fail the trigger invocation.
    /// Defaults to <c>false</c>, matching every sibling package - an exception usually signals a
    /// transient failure worth surfacing as a failed invocation.
    /// </summary>
    public bool CatchExceptions { get; set; } = false;

    /// <summary>
    /// Gets or sets whether a message handler explicitly reporting a non-exception failure result
    /// (<see cref="TimerContext.MessageResult"/><c>.IsSuccessful == false</c>, via
    /// <c>UsePresetTopic(...).UseMessageHandlers()</c> dispatch) is escalated into a thrown
    /// <see cref="TimerMessageProcessingException"/>, so the Azure Functions host records a failed
    /// invocation instead of silently completing. Defaults to <c>true</c> (safe-by-default: a
    /// returned failure is escalated; set <c>false</c> to accept a failure result without throwing).
    /// Unlike message-routed batch triggers, an <em>unset</em> <see cref="TimerContext.MessageResult"/>
    /// is never treated as a failure here - a tick consumed directly via <c>UseTick(...)</c> never
    /// touches it at all, so this flag is a no-op for that consumption mode. Note the platform reality
    /// either way: the timer trigger does not retry a failed tick - the next occurrence just runs on
    /// schedule - so this only affects whether the failure is visible (failed-invocation telemetry)
    /// rather than whether it is retried.
    /// </summary>
    public bool RaiseOnFailureStatus { get; set; } = true;
}
