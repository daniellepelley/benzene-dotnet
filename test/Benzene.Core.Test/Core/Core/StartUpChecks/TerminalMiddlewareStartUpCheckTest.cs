using System;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Aws.Lambda.Core;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Aws.Lambda.Sqs;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.MessageHandlers.StartUpChecks;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Benzene.Test.Core.Core.StartUpChecks;

/// <summary>
/// A pipeline that composes cleanly and can never handle anything.
/// </summary>
/// <remarks>
/// <c>UseSqs(sqs =&gt; { })</c> is the case this check exists for, and the reason it needed the
/// pipeline-introspection seam first: the sub-pipeline is built and discarded inside <c>UseSqs</c>,
/// so until <c>PipelineDescriptor</c> existed nothing outside composition could see that it had
/// nothing in it. It deploys, it runs, and it dead-letters every message while the function returns
/// 200 — the failure mode with no failure in it.
/// </remarks>
public class TerminalMiddlewareStartUpCheckTest
{
    private class PassThrough : IMiddleware<string>
    {
        public string Name => "pass-through";
        public Task HandleAsync(string context, Func<Task> next) => next();
    }

    private class Answers : IMiddleware<string>, ITerminalMiddleware
    {
        public string Name => "answers";
        public Task HandleAsync(string context, Func<Task> next) => Task.CompletedTask;
    }

    private class NeedsSomethingMissing : IMiddleware<string>
    {
        public NeedsSomethingMissing(IMissingService missing) { }
        public string Name => "needs-something-missing";
        public Task HandleAsync(string context, Func<Task> next) => next();
    }

    public interface IMissingService { }

    private static ServiceCollection PipelineOf(Action<IMiddlewarePipelineBuilder<string>> configure)
    {
        var services = new ServiceCollection();
        var builder = new MiddlewarePipelineBuilder<string>(new MicrosoftBenzeneServiceContainer(services));
        configure(builder);
        builder.Build();
        return services;
    }

    [Fact]
    public void APipelineOfNothingButDecoratorsIsReported()
    {
        var services = PipelineOf(builder => builder.Use(_ => new PassThrough()).Use(_ => new PassThrough()));

        using var scope = new MicrosoftServiceResolverFactory(services).CreateScope();
        var exception = Assert.Throws<Benzene.Core.Exceptions.BenzeneException>(
            () => new TerminalMiddlewareStartUpCheck().Check(scope));

        Assert.Contains("String pipeline has no terminal middleware", exception.Message);
        // The one false positive this check can produce is someone's own terminal middleware that
        // isn't marked, so the remedy for that has to be in the message and not only in the docs.
        Assert.Contains(nameof(ITerminalMiddleware), exception.Message);
    }

    [Fact]
    public void OneTerminalAnywhereInThePipelineIsEnough()
    {
        var services = PipelineOf(builder => builder
            .Use(_ => new Answers())
            .Use(_ => new PassThrough()));

        using var scope = new MicrosoftServiceResolverFactory(services).CreateScope();

        new TerminalMiddlewareStartUpCheck().Check(scope);
    }

    [Fact]
    public void AnEmptyPipelineIsLeftAlone()
    {
        // Deliberate scaffolding, not a mistake — and reporting it would fire on every test double
        // that stands a pipeline up without ever sending it a message.
        var services = PipelineOf(_ => { });

        using var scope = new MicrosoftServiceResolverFactory(services).CreateScope();

        new TerminalMiddlewareStartUpCheck().Check(scope);
    }

    [Fact]
    public void APipelineThatCannotBeConstructedIsLeftToTheResolutionCheck()
    {
        // Middleware that won't construct might have been the terminal one. pipeline-resolution
        // already reports it, and names the dependency that was missing; a second, guessed failure
        // beside it would be noise at best and wrong at worst.
        var services = PipelineOf(builder => builder
            .Use(resolver => new NeedsSomethingMissing(resolver.GetService<IMissingService>())));

        using var scope = new MicrosoftServiceResolverFactory(services).CreateScope();

        new TerminalMiddlewareStartUpCheck().Check(scope);
    }

    [Fact]
    public void InlineTerminalMiddlewareCanSayThatItIsOne()
    {
        // A lambda's intent can't be read from its type, so UseTerminal is how it gets said. The
        // framework's own health-check endpoints go through this.
        var services = new ServiceCollection();
        var builder = new MiddlewarePipelineBuilder<string>(new MicrosoftBenzeneServiceContainer(services));
        builder.UseTerminal("answers-inline", (_, _) => Task.CompletedTask);
        builder.Build();

        using var scope = new MicrosoftServiceResolverFactory(services).CreateScope();

        new TerminalMiddlewareStartUpCheck().Check(scope);
    }

    [Fact]
    public void AnEmptySqsPipelineFailsStartUp_RatherThanDeadLetteringTheQueue()
    {
        // The whole point. Before this, UseSqs(sqs => { }) started, ran, and returned 200 for every
        // batch while SQS redrove each message to the DLQ — no exception, no failed batch item, no
        // log line, and a service reporting itself healthy the entire time.
        var exception = Assert.Throws<BenzeneStartUpCheckException>(() => new InlineAwsLambdaStartUp()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddScoped(_ => Mock.Of<IExampleService>());
                services.UsingBenzene(x => x
                    .AddBenzene()
                    .AddMessageHandlers(typeof(Defaults).Assembly)
                    .AddSqs());
            })
            .Configure(app => app.UseSqs(sqs => { }))
            .BuildHost());

        Assert.Contains("terminal-middleware", exception.FailedChecks);
        Assert.Contains("SqsMessageContext pipeline has no terminal middleware", exception.Message);
    }

    [Fact]
    public void AWiredSqsPipelinePasses()
    {
        var host = new InlineAwsLambdaStartUp()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddScoped(_ => Mock.Of<IExampleService>());
                services.UsingBenzene(x => x
                    .AddBenzene()
                    .AddMessageHandlers(typeof(Defaults).Assembly)
                    .AddSqs());
            })
            .Configure(app => app.UseSqs(sqs => sqs.UseMessageHandlers()))
            .BuildHost();

        Assert.NotNull(host);
    }

    [Fact]
    public void TheCheckIsRegisteredAlongsideTheOthers()
    {
        var services = new ServiceCollection();
        new MicrosoftBenzeneServiceContainer(services)
            .AddBenzene()
            .AddMessageHandlers(new[] { typeof(ExampleMessageHandler) });

        using var scope = new MicrosoftServiceResolverFactory(services).CreateScope();

        Assert.Contains("terminal-middleware", scope.GetServices<IStartUpCheck>().Select(x => x.Name));
    }
}
