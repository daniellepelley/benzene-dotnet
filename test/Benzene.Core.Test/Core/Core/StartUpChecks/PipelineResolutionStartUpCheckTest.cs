using System;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Pipelines;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.MessageHandlers.StartUpChecks;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Core.Core.StartUpChecks;

/// <summary>
/// The pipeline-introspection seam, and the check it exists to make possible.
/// </summary>
/// <remarks>
/// <c>MiddlewarePipeline.CreateChain</c> resolves each middleware inside its own link's closure, so
/// nothing is constructed until a message arrives — a pipeline can be unable to run and look
/// perfectly healthy until traffic hits it. The builder that knew the middleware is discarded at
/// <c>Build()</c>, and the pipeline it returns cannot be enumerated, so nothing downstream could see
/// the tree. These tests cover the descriptor that fixes that and the check that walks it.
/// </remarks>
public class PipelineResolutionStartUpCheckTest
{
    private class NeedsSomethingMissing : IMiddleware<string>
    {
        public NeedsSomethingMissing(IMissingService missing) { }
        public string Name => "needs-something-missing";
        public Task HandleAsync(string context, Func<Task> next) => next();
    }

    public interface IMissingService { }

    private class Harmless : IMiddleware<string>
    {
        public string Name => "harmless";
        public Task HandleAsync(string context, Func<Task> next) => next();
    }

    [Fact]
    public void EveryPipelineIsPublished_IncludingSubPipelines()
    {
        // A transport's per-message sub-pipeline is created through the same builder, so it lands in
        // the same container. Without that, the check would only ever see the outer pipeline — and the
        // interesting failures live in the sub-pipelines.
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        var outer = new MiddlewarePipelineBuilder<string>(container);

        outer.Use(_ => new Harmless());
        outer.CreateMiddlewarePipeline<int>(inner => inner.Use(_ => new HarmlessInt()));
        outer.Build();

        using var scope = new MicrosoftServiceResolverFactory(services).CreateScope();
        var descriptors = scope.GetServices<PipelineDescriptor>().ToArray();

        Assert.Equal(2, descriptors.Length);
        Assert.Contains(descriptors, x => x.ContextType == typeof(string));
        Assert.Contains(descriptors, x => x.ContextType == typeof(int));
    }

    private class HarmlessInt : IMiddleware<int>
    {
        public string Name => "harmless-int";
        public Task HandleAsync(int context, Func<Task> next) => next();
    }

    [Fact]
    public void AnUnconstructibleMiddlewareFailsStartUp_NamingWhereItIs()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        var builder = new MiddlewarePipelineBuilder<string>(container);

        builder.Use(_ => new Harmless());
        builder.Use(resolver => new NeedsSomethingMissing(resolver.GetService<IMissingService>()));
        builder.Build();

        using var scope = new MicrosoftServiceResolverFactory(services).CreateScope();
        var exception = Assert.Throws<Benzene.Core.Exceptions.BenzeneException>(
            () => new PipelineResolutionStartUpCheck().Check(scope));

        // A failed construction has no instance to take a name from, so position is the most specific
        // thing that exists — and it is enough to find the line.
        Assert.Contains("String pipeline, middleware #2", exception.Message);
        Assert.Contains(nameof(IMissingService), exception.Message);
    }

    [Fact]
    public void AHealthyPipelinePasses()
    {
        var services = new ServiceCollection();
        var container = new MicrosoftBenzeneServiceContainer(services);
        var builder = new MiddlewarePipelineBuilder<string>(container);
        builder.Use(_ => new Harmless());
        builder.Build();

        using var scope = new MicrosoftServiceResolverFactory(services).CreateScope();

        new PipelineResolutionStartUpCheck().Check(scope);
    }

    [Fact]
    public void AClientPipelineWithNoContainerIsNotPublished()
    {
        // The outbound clients (SNS, Service Bus, RabbitMQ, …) build a self-contained pipeline out of
        // instances they already hold, against a null-object container that throws on registration.
        // Publishing a descriptor there threw NotImplementedException and took 22 client tests with
        // it — so Build() asks whether the container accepts registrations rather than finding out.
        var builder = new MiddlewarePipelineBuilder<string>(new NullBenzeneServiceContainer());
        builder.Use(_ => new Harmless());

        var pipeline = builder.Build();

        Assert.NotNull(pipeline);
    }

    [Fact]
    public void AllThreeCoreChecksAreRegistered_NotJustTheFirst()
    {
        // The bug this test exists for: TryAddSingleton<IStartUpCheck, X> de-duplicates by *service*
        // type, so registering three implementations of one collection service kept only the first.
        // Two of the three checks silently did not exist, and nothing said so — the phase looked wired
        // and was mostly inert.
        var services = new ServiceCollection();
        new MicrosoftBenzeneServiceContainer(services)
            .AddBenzene()
            .AddMessageHandlers(new[] { typeof(ExampleMessageHandler) });

        using var scope = new MicrosoftServiceResolverFactory(services).CreateScope();
        var names = scope.GetServices<IStartUpCheck>().Select(x => x.Name).ToArray();

        Assert.Contains("duplicate-topic", names);
        Assert.Contains("empty-handler-registry", names);
        Assert.Contains("pipeline-resolution", names);
    }
}
