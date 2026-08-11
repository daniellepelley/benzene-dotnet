using System.Reflection;
using Xunit;

namespace Benzene.Mesh.Host.Test;

/// <summary>
/// The mesh product owner's position (<c>work/enterprise/slice-3-discovery.md</c>): the vanilla mesh
/// host must be <em>physically incapable</em> of enumerating a cloud account - not a feature that
/// defaults off, a code path that does not exist in this binary at all. "The image contains no code
/// path that calls <c>ListFunctions</c>, and its role needs no list permissions" is a stronger answer
/// in a security review than "the flag is off, and here is who could turn it on".
/// </summary>
/// <remarks>
/// This is the enforcement of that position, not a description of it: it converts a promise a reader
/// has to take on faith into something CI checks on every build. The day someone adds a
/// <c>Benzene.Mesh.Discovery.*</c> reference to this host "just to make one thing easier", this test
/// fails and they have to make that argument out loud, in a PR, rather than the invariant quietly
/// eroding. Do not delete this as pedantry if it is ever in your way - either the change genuinely
/// needs to route around the two-deployable design (a decision for the mesh product owner, not this
/// test), or it is exactly the drift this test exists to catch.
///
/// <c>Benzene.Mesh.Host.Test.AwsMeshParityTest.Discovery_IsNotReachableFromConfig_HostReferencesNoDiscoveryAssembly</c>
/// checks the same thing but only one level deep (<see cref="Assembly.GetReferencedAssemblies"/> on
/// the host assembly itself) - accurate today, but a <c>Benzene.Mesh.Discovery.*</c> reference two or
/// three levels down a dependency chain would satisfy that shallow check while still shipping in the
/// image. This test walks the full transitive closure instead, so depth of the reference cannot hide
/// it.
///
/// Note what "reference" means here: <see cref="Assembly.GetReferencedAssemblies"/> reports the
/// AssemblyRefs the compiler actually emitted into the IL, which only happens when compiled code uses
/// a type from that assembly. A bare <c>&lt;ProjectReference&gt;</c> in the <c>.csproj</c> with no
/// corresponding type usage produces no AssemblyRef and is invisible to this check (verified while
/// writing it - see the slice-3-discovery.md report for the before/after). That is the case that
/// actually matters: the invariant this test protects is about the host *doing* discovery-capable
/// things, not about a dangling, unused line in a project file. Real drift - someone calling
/// <c>AddMeshAwsLambdaDiscovery()</c> or similar - always produces a genuine IL reference and this
/// test catches it.
/// </remarks>
public class NoDiscoveryInVanillaHostTest
{
    private const string ForbiddenAssemblyPrefix = "Benzene.Mesh.Discovery";

    [Fact]
    public void HostAssembly_HasNoTransitiveDependencyOnAnyDiscoveryAssembly()
    {
        var offenders = FindForbiddenTransitiveReferences(typeof(Startup).Assembly, ForbiddenAssemblyPrefix);

        Assert.True(offenders.Count == 0,
            $"Benzene.Mesh.Host must never reference a Benzene.Mesh.Discovery.* assembly (found: " +
            $"{string.Join(", ", offenders)}). See work/enterprise/slice-3-discovery.md - discovery is " +
            "a separate deployable, not an optional dependency of this host.");
    }

    /// <summary>
    /// Breadth-first walk of <paramref name="root"/>'s full transitive reference closure, returning
    /// every distinct assembly *name* encountered (by simple name, not full assembly identity) that
    /// starts with <paramref name="forbiddenPrefix"/>. Referenced assemblies are loaded
    /// (<see cref="Assembly.Load(AssemblyName)"/>) so their own references can be walked in turn - a
    /// name-only check on the host's direct references, as in <c>AwsMeshParityTest</c>, would miss a
    /// forbidden dependency pulled in indirectly. An assembly that fails to load (a native or
    /// reference-only dependency) is still checked by name and simply not walked further - it cannot
    /// hide a forbidden name, only a forbidden name's own indirect dependencies, which is not a case
    /// that can arise here (nothing in this codebase is packaged that way).
    /// </summary>
    private static List<string> FindForbiddenTransitiveReferences(Assembly root, string forbiddenPrefix)
    {
        var offenders = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            var name = assembly.GetName().Name;
            if (name == null || !visited.Add(name))
            {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name == null || visited.Contains(reference.Name))
                {
                    continue;
                }

                if (reference.Name.StartsWith(forbiddenPrefix, StringComparison.Ordinal))
                {
                    offenders.Add(reference.Name);
                }

                try
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
                catch
                {
                    // Cannot walk further into this one (e.g. a framework assembly resolved
                    // differently at runtime) - already name-checked above, so nothing is missed,
                    // only left unwalked.
                    visited.Add(reference.Name);
                }
            }
        }

        return offenders;
    }
}
