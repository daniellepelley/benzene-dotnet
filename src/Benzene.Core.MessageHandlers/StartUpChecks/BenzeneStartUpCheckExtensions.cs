using Benzene.Abstractions.DI;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace Benzene.Core.MessageHandlers.StartUpChecks;

/// <summary>
/// Registration and the host-side runner for start-up checks.
/// </summary>
/// <remarks>
/// <para>
/// Benzene already had five wiring checks in five packages, each opt-in, each with a different name
/// and severity, and none of them called by any host. The one mechanism that did run at Lambda INIT —
/// warm-up — swallowed every exception, so even the duplicate-topic error it already discovered was
/// thrown away and re-thrown later on the first message. This is the phase those checks were missing:
/// one place, run by every host, with failures that survive.
/// </para>
/// </remarks>
public static class BenzeneStartUpCheckExtensions
{
    /// <summary>
    /// Sets what a failing check does. Checks are registered by <c>AddBenzene()</c> and enforced by
    /// default; call this only to soften or silence them.
    /// </summary>
    /// <param name="services">The service container to register into.</param>
    /// <param name="mode">Enforce (default), Advisory (log and continue), or Disabled.</param>
    /// <returns>The same container, for chaining.</returns>
    public static IBenzeneServiceContainer AddBenzeneStartUpChecks(
        this IBenzeneServiceContainer services, BenzeneStartUpCheckMode mode = BenzeneStartUpCheckMode.Enforce)
    {
        services.AddSingleton(_ => new BenzeneStartUpCheckOptions(mode));
        return services;
    }

    /// <summary>
    /// Runs the checks and returns the factory, for use where the factory is built inline.
    /// </summary>
    /// <remarks>
    /// The <c>Build*</c> test-host extensions construct their factory in a return expression, and each
    /// one exists to perform "the same construction the real host does". The checks are part of that
    /// construction, so this keeps them in it — which is what makes a wiring bug a red unit test rather
    /// than something the developer meets on a deployed function.
    /// </remarks>
    /// <typeparam name="TFactory">The concrete factory type, returned unchanged.</typeparam>
    /// <param name="factory">The factory to check through.</param>
    /// <returns><paramref name="factory"/>.</returns>
    public static TFactory WithStartUpChecks<TFactory>(this TFactory factory)
        where TFactory : IServiceResolverFactory
    {
        factory.RunStartUpChecks();
        return factory;
    }

    /// <summary>
    /// Runs every registered <see cref="IStartUpCheck"/> once, on a throwaway scope.
    /// </summary>
    /// <remarks>
    /// Called by every host from its initialization, so a wiring bug is found at INIT rather than by
    /// the first message that happens to reach the broken link. <c>BenzeneTestHost</c> calls it too,
    /// which makes the same bug a red unit test — the cheapest place of all to find it.
    /// </remarks>
    /// <param name="factory">The service resolver factory to check through.</param>
    /// <exception cref="BenzeneStartUpCheckException">
    /// One or more checks failed and the mode is <see cref="BenzeneStartUpCheckMode.Enforce"/>.
    /// </exception>
    public static void RunStartUpChecks(this IServiceResolverFactory factory)
    {
        using var resolver = factory.CreateScope();

        var mode = resolver.TryGetService<BenzeneStartUpCheckOptions>()?.Mode ?? BenzeneStartUpCheckMode.Enforce;
        if (mode == BenzeneStartUpCheckMode.Disabled)
        {
            return;
        }

        var failures = new List<(string Name, Exception Error)>();

        foreach (var check in resolver.GetServices<IStartUpCheck>())
        {
            try
            {
                check.Check(resolver);
            }
            catch (Exception exception)
            {
                failures.Add((check.Name, exception));
            }
        }

        if (failures.Count == 0)
        {
            return;
        }

        // Every failure is reported, not just the first. A wiring mistake often trips several checks,
        // and fixing them one round-trip at a time is the friction this whole phase exists to remove.
        if (mode == BenzeneStartUpCheckMode.Advisory)
        {
            var logger = resolver.TryGetService<ILoggerFactory>()?.CreateLogger("Benzene.StartUpChecks");
            foreach (var (name, error) in failures)
            {
                logger?.LogError(error, "Benzene start-up check '{Check}' failed: {Message}", name, error.Message);
            }

            return;
        }

        throw new BenzeneStartUpCheckException(failures);
    }
}

/// <summary>
/// Raised when one or more start-up checks fail in <see cref="BenzeneStartUpCheckMode.Enforce"/>.
/// </summary>
public class BenzeneStartUpCheckException : BenzeneException
{
    internal BenzeneStartUpCheckException(IReadOnlyCollection<(string Name, Exception Error)> failures)
        : base(Describe(failures), failures.Count == 1 ? failures.First().Error : new AggregateException(failures.Select(x => x.Error)))
    {
        FailedChecks = failures.Select(x => x.Name).ToArray();
    }

    /// <summary>The names of the checks that failed.</summary>
    public string[] FailedChecks { get; }

    private static string Describe(IReadOnlyCollection<(string Name, Exception Error)> failures)
    {
        var lines = failures.Select(x => $"  - {x.Name}: {x.Error.Message}");

        return $"Benzene found {failures.Count} wiring problem(s) at start-up:{Environment.NewLine}" +
               string.Join(Environment.NewLine, lines) + Environment.NewLine + Environment.NewLine +
               "These are checked before any message is handled so they don't surface as a failure on the " +
               "message path later. If a check is wrong for your application, soften or silence all of them " +
               "with .AddBenzeneStartUpChecks(BenzeneStartUpCheckMode.Advisory) (or .Disabled).";
    }
}
