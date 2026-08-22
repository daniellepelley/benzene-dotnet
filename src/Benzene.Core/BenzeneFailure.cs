using Benzene.Core.Exceptions;

namespace Benzene.Core;

/// <summary>
/// Tells an infrastructure failure from a business one at the transport boundary.
/// </summary>
/// <remarks>
/// <para>
/// The two need opposite handling and used to get identical handling. A business failure — a handler
/// throwing on one payload, a downstream timing out — is that message's problem, and per-message
/// retry then dead-letter is exactly right. An infrastructure failure is a missing registration: it
/// affects every message equally, it is never the message's fault, and retrying cannot fix it. Treated
/// per-message, it dead-letters the entire queue one record at a time while the service reports
/// itself healthy.
/// </para>
/// <para>
/// Deliberately narrow. Only a failure that is <em>provably</em> about wiring counts, because the cost
/// of a false positive is failing a whole batch over one bad message. Anything unrecognised is
/// business — the existing, safe behaviour.
/// </para>
/// </remarks>
public static class BenzeneFailure
{
    /// <summary>
    /// Whether this failure is about the service's wiring rather than the message it was handling.
    /// </summary>
    /// <param name="exception">The exception caught at the transport boundary.</param>
    /// <returns><c>true</c> for a resolution failure anywhere in the chain.</returns>
    public static bool IsInfrastructure(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            // A type check, not a message match. Both resolver adapters throw this specific type, so
            // the classification cannot drift with the wording of an error string.
            if (current is BenzeneResolutionException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A greppable prefix for the log line, so a wiring failure can be found without reading stack
    /// traces and separated from the per-message noise around it.
    /// </summary>
    public const string InfrastructureLogPrefix = "[benzene:infrastructure]";
}
