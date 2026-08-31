using System;

namespace Benzene.Azure.Function.Timer;

/// <summary>
/// Thrown by <see cref="TimerApplication"/> when <see cref="TimerOptions.RaiseOnFailureStatus"/> is
/// enabled and the tick's pipeline reported an unsuccessful result without itself throwing -
/// escalating the failure into an exception so the Azure Functions host records a failed invocation
/// the same way it would for an unhandled exception.
/// </summary>
public class TimerMessageProcessingException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TimerMessageProcessingException"/> class.</summary>
    /// <param name="scheduledFor">
    /// The tick's next scheduled occurrence (<c>TimerTriggerInfo.ScheduleStatus.Next</c>), or
    /// <c>null</c> when the host provided no schedule status.
    /// </param>
    public TimerMessageProcessingException(DateTimeOffset? scheduledFor)
        : base($"Message handler reported an unsuccessful result for the timer tick scheduled for {scheduledFor}.")
    {
        ScheduledFor = scheduledFor;
    }

    /// <summary>Gets the tick's next scheduled occurrence the handler reported a failure for.</summary>
    public DateTimeOffset? ScheduledFor { get; }
}
