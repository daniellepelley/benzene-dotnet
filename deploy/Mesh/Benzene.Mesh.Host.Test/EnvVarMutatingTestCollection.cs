using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// Groups every test class that sets/restores <c>MESH_BASIC_USER</c>/<c>MESH_BASIC_PASSWORD</c>/
/// <c>MESH_OIDC_CLIENT_SECRET</c>/<c>MESH_INGEST_SECRET</c> - real, process-wide environment
/// variables, not per-test state - into one xUnit collection so xUnit runs them sequentially instead
/// of in parallel (facts within one class already run sequentially by default; it is DIFFERENT
/// classes racing each other on the same env var that this collection closes).
/// </summary>
/// <remarks>
/// WP-1 (work/bug-fix-designs-2026-08.md "WP-1") added enough new <c>MESH_OIDC_CLIENT_SECRET</c>
/// mutators (<see cref="MeshAuthGateTest"/>'s new satisfiability-matrix/https-metadata cases,
/// <see cref="MeshAuthLogoutAcceptanceTest"/>, <see cref="MeshUiWiringAcceptanceTest"/>) that the
/// pre-existing race between a <see cref="MeshAuthGateTest"/> fact's synchronous set-then-restore and
/// an acceptance test's async <c>host.StartAsync()</c> holding the same variable across an await went
/// from theoretical to observed (an acceptance test's real Kestrel host failing to start with "auth.mode
/// 'oidc' requires ... MESH_OIDC_CLIENT_SECRET ... to be set" because a parallel
/// <see cref="MeshAuthGateTest"/> fact cleared it mid-flight). Every class that touches any of these
/// four variables carries <c>[Collection(Name)]</c> - add any future one here too.
/// </remarks>
[CollectionDefinition(Name)]
public class EnvVarMutatingTestCollection
{
    public const string Name = "Mesh host env-var mutators";
}
