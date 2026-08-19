using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Hosting;

namespace Benzene.HostedService;

/// <summary>
/// Runs a <see cref="BenzeneStartUp"/> on the .NET generic host, so a Benzene service's entry point
/// is one line.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HostBuilderExtensions.UseBenzene{TStartUp}"/> already reduces a Benzene worker's
/// <c>Program.cs</c> to building a host and running it. This removes the remaining ceremony:
/// </para>
/// <code>
/// // Program.cs, entire
/// await BenzeneHost.RunAsync&lt;StartUp&gt;(args);
/// </code>
/// <para>
/// The point is not the line count. It is that <b>the startup becomes the only place hosting is
/// described</b> — transports are declared in <c>Configure</c> via <c>UseWorker(...)</c>
/// (<c>UseAspNet</c>, <c>UseSqs</c>, <c>UseKafka</c>, <c>UseRabbitMq</c>, …), so adding one later
/// never touches the entry point. A <c>Program.cs</c> that mentions a specific transport is a
/// <c>Program.cs</c> that has to be rewritten when the service grows another one.
/// </para>
/// <para>
/// <b>When NOT to use it.</b> This owns the host, so it is for a service whose host is Benzene's to
/// own. If the process is a larger ASP.NET Core application with its own controllers or minimal APIs,
/// embed Benzene in it instead — <c>WebApplicationBuilder.UseBenzene&lt;TStartUp&gt;()</c> plus
/// <c>app.UseBenzene()</c> — and if the host needs shaping this does not cover, use
/// <see cref="Build{TStartUp}"/> or drop to <c>UseBenzene&lt;TStartUp&gt;()</c> on a builder you own.
/// Nothing here is load-bearing; it is a shorthand for the common case.
/// </para>
/// <para>
/// The host is <see cref="Host.CreateDefaultBuilder(string[])"/>, so a service gets the conventional
/// content root, configuration sources and logging providers. Note that
/// <see cref="BenzeneStartUp.GetConfiguration"/> builds its OWN configuration and is not handed the
/// host's — that is the existing contract, unchanged here.
/// </para>
/// </remarks>
public static class BenzeneHost
{
    /// <summary>Builds the host for <typeparamref name="TStartUp"/> without running it.</summary>
    /// <typeparam name="TStartUp">The startup describing the service's services and transports.</typeparam>
    /// <param name="args">Command-line arguments, passed to <see cref="Host.CreateDefaultBuilder(string[])"/>.</param>
    /// <param name="configureHost">
    /// Applied to the builder <b>before</b> <c>UseBenzene&lt;TStartUp&gt;()</c>.
    /// </param>
    /// <returns>The built host, not yet started.</returns>
    /// <remarks>
    /// The escape hatch, and the seam a test uses: it hands back the <see cref="IHost"/> so the caller
    /// can resolve services from it, start and stop it by hand, or inspect what the startup
    /// registered.
    /// <para>
    /// <paramref name="configureHost"/> runs first on purpose. Benzene's baseline services are
    /// <c>TryAdd</c> (first registration wins) — <c>AddBenzene</c> says as much on its
    /// <c>ISerializer</c> registration — so a caller substituting one has to land before the startup
    /// runs. It is the same ordering rule <c>InlineSelfHostedStartUp</c> follows, for the same reason.
    /// </para>
    /// <para>
    /// The corollary, worth knowing before it surprises you: this does <b>not</b> override what
    /// <typeparamref name="TStartUp"/> <em>itself</em> registers with a plain <c>Add</c>, because that
    /// lands afterwards and Microsoft DI resolves the last registration. That is the right way round —
    /// the startup is the caller's own code, while Benzene's internal defaults are the thing they
    /// cannot otherwise reach — but it means this is a hook for shaping the <em>host</em>, not for
    /// overruling the startup.
    /// </para>
    /// </remarks>
    public static IHost Build<TStartUp>(string[]? args = null, Action<IHostBuilder>? configureHost = null)
        where TStartUp : BenzeneStartUp, new()
    {
        var hostBuilder = Host.CreateDefaultBuilder(args ?? Array.Empty<string>());
        configureHost?.Invoke(hostBuilder);
        return hostBuilder.UseBenzene<TStartUp>().Build();
    }

    /// <summary>Builds and runs the host, returning when it shuts down.</summary>
    /// <typeparam name="TStartUp">The startup describing the service's services and transports.</typeparam>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="configureHost">Applied to the builder before <c>UseBenzene&lt;TStartUp&gt;()</c>.</param>
    /// <param name="cancellationToken">Triggers shutdown, in addition to the host's own signals.</param>
    /// <remarks>
    /// Shutdown is the generic host's: SIGTERM/Ctrl-C stop the host, which stops every
    /// <see cref="IHostedService"/> — including the <see cref="BenzeneHostedServiceAdapter"/> wrapping
    /// the service's workers, whose own bounded drain then runs. Nothing about that changes here.
    /// </remarks>
    public static Task RunAsync<TStartUp>(string[]? args = null, Action<IHostBuilder>? configureHost = null,
        CancellationToken cancellationToken = default)
        where TStartUp : BenzeneStartUp, new()
        => Build<TStartUp>(args, configureHost).RunAsync(cancellationToken);

    /// <summary>
    /// Builds and runs the host, blocking until it shuts down — the synchronous counterpart of
    /// <see cref="RunAsync{TStartUp}"/>, mirroring <see cref="HostingAbstractionsHostExtensions.Run"/>.
    /// </summary>
    /// <typeparam name="TStartUp">The startup describing the service's services and transports.</typeparam>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="configureHost">Applied to the builder before <c>UseBenzene&lt;TStartUp&gt;()</c>.</param>
    public static void Run<TStartUp>(string[]? args = null, Action<IHostBuilder>? configureHost = null)
        where TStartUp : BenzeneStartUp, new()
        => Build<TStartUp>(args, configureHost).Run();
}
