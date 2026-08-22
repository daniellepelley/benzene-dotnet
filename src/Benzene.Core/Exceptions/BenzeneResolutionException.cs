namespace Benzene.Core.Exceptions;

/// <summary>
/// A service could not be resolved from the container.
/// </summary>
/// <remarks>
/// <para>
/// Its own type is the point. This is an <em>infrastructure</em> failure: the container is missing a
/// registration, so it will fail identically for every message, and no amount of retrying or
/// redelivery will change that. A business failure — a handler rejecting a payload, a downstream
/// timing out — is the opposite on both counts, and the two used to be indistinguishable at the
/// transport boundary, where the only thing available was a <see cref="BenzeneException"/> with a
/// message to string-match against.
/// </para>
/// <para>
/// <see cref="Benzene.Core.BenzeneFailure"/> keys off this type, which is why the transports can now
/// treat a wiring failure as the whole-invocation problem it is rather than dead-lettering one
/// message at a time forever.
/// </para>
/// </remarks>
public class BenzeneResolutionException : BenzeneException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">What could not be resolved.</param>
    /// <param name="innerException">The container's own failure.</param>
    public BenzeneResolutionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
