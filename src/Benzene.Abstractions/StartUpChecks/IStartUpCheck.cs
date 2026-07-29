using Benzene.Abstractions.DI;

namespace Benzene.Abstractions.StartUpChecks;

/// <summary>
/// A wiring check run once during host initialization, before any message is handled.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="Benzene.Abstractions.WarmUp.IWarmUpTask"/>, and deliberately its
/// opposite in the one way that matters: <b>a check's exception propagates</b>. Warm-up is
/// best-effort, so its runner swallows failures — correct for warming, fatal for checking. A check
/// that found a real wiring bug and then had its exception discarded is worse than no check, because
/// the bug still surfaces later, on the message path, with the evidence thrown away.
/// </para>
/// <para>
/// A check runs on a throwaway scope, off the request path, exactly once. It must not dispatch a
/// message or produce side effects — it inspects registrations.
/// </para>
/// <para>
/// Checks obey a single kill switch (<c>BenzeneStartUpCheckMode</c>). A newcomer who hits a check
/// they believe is wrong must be able to turn the whole thing off in one line, or they will abandon
/// Benzene rather than debug the thing meant to help them debug it.
/// </para>
/// </remarks>
public interface IStartUpCheck
{
    /// <summary>A short name for this check, used when reporting what failed.</summary>
    string Name { get; }

    /// <summary>
    /// Runs the check. Throw to report a wiring error; the runner decides, from the configured mode,
    /// whether that fails start-up or is logged.
    /// </summary>
    /// <param name="resolver">A throwaway scope to inspect registrations through.</param>
    void Check(IServiceResolver resolver);
}
