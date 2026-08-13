using System.Threading;

namespace Benzene.Abstractions.DI;

/// <summary>
/// Scope-level access to the ambient <see cref="CancellationToken"/> for the current unit of work,
/// so any component resolved from the scope (a health check, a handler, an outbound client) can
/// observe cancellation without the pipeline threading a token through every method signature.
/// Mirrors how ASP.NET Core exposes the request-aborted token via <c>IHttpContextAccessor</c>.
/// </summary>
/// <remarks>
/// <para>
/// Registered scoped. Defaults to <see cref="CancellationToken.None"/> until a transport (or a
/// component such as the health-check processor) seeds it for the scope. Read-only here; the seeding
/// side sets it via the concrete accessor.
/// </para>
/// <para>
/// <b>The guarantee:</b> the token defaults to <see cref="CancellationToken.None"/>. A handler,
/// middleware, or component that never resolves <see cref="ICancellationTokenAccessor"/> behaves
/// byte-for-byte as before - no new exceptions, no new statuses, no timing changes. A component that
/// does resolve it must treat the token as <i>advisory and possibly <see cref="CancellationToken.None"/></i>:
/// on transports with no cancellation concept it simply never fires, and code written as
/// <c>await client.DoAsync(x, accessor.CancellationToken)</c> is correct everywhere without checking
/// which host it runs on.
/// </para>
/// <para>
/// <b>Read at the point of use.</b> Always read <see cref="CancellationToken"/> at the moment of
/// use (a property access, not a value captured at construction time) - wrapping middleware (for
/// example a timeout middleware) may replace the ambient token for the duration of an inner call via
/// a save/restore pattern, so a value captured earlier can be stale by the time it matters.
/// </para>
/// </remarks>
public interface ICancellationTokenAccessor
{
    /// <summary>The cancellation token for the current scope, or <see cref="CancellationToken.None"/> when none has been set.</summary>
    CancellationToken CancellationToken { get; }
}
