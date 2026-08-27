using Benzene.Mesh.Auth.Oidc;
using Benzene.Mesh.Dispatch;

namespace Benzene.Examples.AwsMesh.Mesh;

/// <summary>
/// Carries the signed-in caller from the OIDC session gate to the dispatch guard and the audit record.
/// </summary>
/// <remarks>
/// <para>
/// Lives in the host rather than in either package, and deliberately: <c>Benzene.Mesh.Auth.Oidc</c>
/// must not know that dispatch exists, and <c>Benzene.Mesh.Artifacts</c> must not depend on one
/// particular way of authenticating a caller. Joining two independent capabilities is the host's job,
/// and this is what that job looks like when neither side is bent to fit the other.
/// </para>
/// <para>
/// Both sides are scoped (this sink is registered <c>AddScoped</c> in <c>Startup.cs</c>, and
/// <c>OidcSessionGateMiddleware</c> is itself registered scoped in <c>Extensions.cs</c>), so this
/// writes the identity for exactly the request the gate validated - not some other request's. That
/// second half was a genuine bug until Benzene.Mesh.Auth.Oidc's #172 fix: the gate used to be
/// registered as a SINGLETON despite taking this (correctly scoped) sink through its constructor,
/// which meant the container resolved this sink exactly once, at the very first request's scope, and
/// held that one instance - and the <see cref="MeshDispatchIdentity"/> it wraps - for the rest of the
/// container's lifetime. Every later request's <see cref="Authenticated"/> call was silently
/// overwriting the FIRST request's identity object, not its own; nothing here needed to change to fix
/// it, since this class was never the broken half.
/// </para>
/// </remarks>
public class OidcDispatchIdentitySink : IOidcSessionSink
{
    private readonly MeshDispatchIdentity _identity;

    /// <summary>Initializes a new instance of the <see cref="OidcDispatchIdentitySink"/> class.</summary>
    public OidcDispatchIdentitySink(MeshDispatchIdentity identity)
    {
        _identity = identity;
    }

    /// <inheritdoc />
    public void Authenticated(string email)
    {
        _identity.Email = email;
        // The environment label the audit record carries. Read from the host's own configuration
        // rather than inferred, and stated as unknown when unset — the same rule the UI's environment
        // chip follows, for the same reason.
        _identity.Environment = System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
    }
}
