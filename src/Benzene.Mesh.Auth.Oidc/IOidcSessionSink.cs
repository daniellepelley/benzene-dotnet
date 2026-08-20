namespace Benzene.Mesh.Auth.Oidc;

/// <summary>
/// Receives the caller's identity once the session gate has validated it.
/// </summary>
/// <remarks>
/// <para>
/// The gate validated the caller's email and then dropped it, so nothing downstream could say who
/// had done anything. That is fine for a gate whose only job is to admit or refuse — and not fine the
/// moment a surface below it acts on the caller's behalf, because an audit record with no subject is
/// not an audit record.
/// </para>
/// <para>
/// Deliberately a sink rather than a return value or a well-known context key: the gate should not
/// have to know what anything downstream wants the identity FOR. Nothing registers an implementation
/// by default, so this costs an unresolved optional dependency and no behaviour at all until a
/// package that needs attribution — the dispatch guard, today — supplies one.
/// </para>
/// </remarks>
public interface IOidcSessionSink
{
    /// <summary>
    /// Called with the validated, still-allowlisted email, before the request continues. Never called
    /// for a refused request.
    /// </summary>
    /// <param name="email">The signed-in caller.</param>
    void Authenticated(string email);
}
