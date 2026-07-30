using Benzene.Abstractions.DI;

namespace Benzene.Abstractions.Pipelines;

/// <summary>
/// A built pipeline, described so something outside it can walk it.
/// </summary>
/// <remarks>
/// <para>
/// <c>MiddlewarePipelineBuilder.Build()</c> returns an <c>IMiddlewarePipeline&lt;TContext&gt;</c> that
/// exposes no way to enumerate its items, and the builder that knew them is discarded. Every
/// transport's per-message sub-pipeline (<c>UseSqs</c>, <c>UseApiGateway</c>, …) goes through that
/// same path, so nothing downstream of composition could see the pipeline tree — which is why a
/// pipeline that cannot construct its middleware, or has no terminal middleware at all, could only be
/// discovered by sending it a message.
/// </para>
/// <para>
/// Each builder now registers one of these into the shared container as it builds, so a start-up
/// check can enumerate every pipeline in the process — outer and sub-pipelines alike — without
/// changing how any of them execute.
/// </para>
/// <para>
/// The middleware factories are erased to <see cref="object"/> deliberately: a check over "every
/// pipeline" cannot be generic over each pipeline's context type, and it only needs to know whether
/// construction succeeds, not what was constructed.
/// </para>
/// </remarks>
public class PipelineDescriptor
{
    /// <summary>
    /// Initializes a descriptor for one built pipeline.
    /// </summary>
    /// <param name="contextType">The pipeline's context type, used to name it when reporting.</param>
    /// <param name="constructors">
    /// One factory per middleware, in pipeline order, each erased to <see cref="object"/>. Calling one
    /// constructs that middleware and nothing else — no message is dispatched.
    /// </param>
    public PipelineDescriptor(Type contextType, IReadOnlyList<Func<IServiceResolver, object>> constructors)
    {
        ContextType = contextType;
        Constructors = constructors;
    }

    /// <summary>The context type this pipeline runs over (e.g. <c>SqsMessageContext</c>).</summary>
    public Type ContextType { get; }

    /// <summary>One factory per middleware, in pipeline order.</summary>
    public IReadOnlyList<Func<IServiceResolver, object>> Constructors { get; }

    /// <summary>A short name for this pipeline, for diagnostics.</summary>
    public string Name => ContextType.Name;
}
