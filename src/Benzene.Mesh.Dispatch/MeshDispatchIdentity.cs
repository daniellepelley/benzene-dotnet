namespace Benzene.Mesh.Dispatch;

/// <summary>
/// Who is dispatching, for the duration of one request.
/// </summary>
/// <remarks>
/// <para>
/// Scoped state, set by <see cref="MeshDispatchGuardMiddleware{TContext}"/> and read by the handler,
/// because the two checks that need an identity sit at different layers: the per-identity rate limit
/// is an HTTP-level concern (it should refuse before anything is parsed), while the audit record and
/// the per-target limit need the parsed target service, which only the handler has.
/// </para>
/// <para>
/// It exists at all because the OIDC session gate validates the caller's email and then discards it —
/// nothing downstream could say who had dispatched, which would have made every audit record blind.
/// </para>
/// </remarks>
public class MeshDispatchIdentity
{
    /// <summary>
    /// The signed-in caller, or null when nothing established one. Null must be treated as a refusal
    /// on any path that acts, never as an anonymous allowance.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>The environment label this host is running as, for the audit record.</summary>
    public string? Environment { get; set; }
}
