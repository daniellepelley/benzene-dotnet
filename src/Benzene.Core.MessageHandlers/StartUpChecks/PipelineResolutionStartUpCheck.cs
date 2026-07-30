using Benzene.Abstractions.DI;
using Benzene.Abstractions.Pipelines;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Core.Exceptions;

namespace Benzene.Core.MessageHandlers.StartUpChecks;

/// <summary>
/// Constructs every middleware in every pipeline, so a missing registration fails start-up instead of
/// the first message that happens to reach that link.
/// </summary>
/// <remarks>
/// <para>
/// <c>MiddlewarePipeline.CreateChain</c> resolves each middleware <em>inside</em> the closure for its
/// own link. That is deliberate and worth keeping — a short-circuited middleware is never
/// constructed, and <c>UseExceptionHandler</c> can cover a downstream construction failure — but it
/// means no middleware's constructor dependencies are touched until a message reaches that link. A
/// pipeline can be completely unable to run and look perfectly healthy until traffic arrives.
/// </para>
/// <para>
/// This check does the resolving once, at start-up, on a throwaway scope. It constructs; it never
/// dispatches. There is no arrangement where a middleware in a mounted pipeline that cannot be
/// constructed is intentional — the first message to reach it fails either way — so it throws.
/// </para>
/// <para>
/// It also catches what <c>ValidateOnBuild</c> structurally cannot: open generics such as
/// <c>MessageRouter&lt;&gt;</c>, and the factory-registered descriptors Benzene uses heavily, neither
/// of which the container validates.
/// </para>
/// </remarks>
public class PipelineResolutionStartUpCheck : IStartUpCheck
{
    /// <inheritdoc />
    public string Name => "pipeline-resolution";

    /// <inheritdoc />
    public void Check(IServiceResolver resolver)
    {
        var failures = new List<string>();

        foreach (var pipeline in resolver.GetServices<PipelineDescriptor>())
        {
            for (var i = 0; i < pipeline.Constructors.Count; i++)
            {
                try
                {
                    pipeline.Constructors[i](resolver);
                }
                catch (Exception exception)
                {
                    // Index and pipeline name, because a failed construction has no instance to take a
                    // name from — position in the pipeline is the most specific thing that exists.
                    failures.Add($"{pipeline.Name} pipeline, middleware #{i + 1}: {Innermost(exception).Message}");
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new BenzeneException(
                $"Middleware in {failures.Count} place(s) cannot be constructed, so the first message to " +
                $"reach it would fail:{Environment.NewLine}  " + string.Join($"{Environment.NewLine}  ", failures));
        }
    }

    // The outer frame says "unable to resolve X"; the innermost says which dependency was actually
    // missing, which is the one the developer has to register.
    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException != null)
        {
            exception = exception.InnerException;
        }

        return exception;
    }
}
