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
/// Both sides are scoped, so this writes the identity for exactly the request the gate validated.
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
