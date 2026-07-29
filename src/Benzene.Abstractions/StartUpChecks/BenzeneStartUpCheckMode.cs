namespace Benzene.Abstractions.StartUpChecks;

/// <summary>
/// What a failing <see cref="IStartUpCheck"/> does. One switch for all of them, on purpose.
/// </summary>
/// <remarks>
/// Fifteen individual toggles would mean a developer facing a check they think is wrong has to work
/// out which one to turn off before they can get on with their day. One switch is the difference
/// between an escape hatch and a maze.
/// </remarks>
public enum BenzeneStartUpCheckMode
{
    /// <summary>A failing check fails start-up. The default.</summary>
    Enforce,

    /// <summary>A failing check is logged as an error and start-up continues.</summary>
    Advisory,

    /// <summary>No checks run at all.</summary>
    Disabled
}

/// <summary>The configured <see cref="BenzeneStartUpCheckMode"/>, resolved by the runner.</summary>
public class BenzeneStartUpCheckOptions
{
    /// <summary>Initializes options in the given mode.</summary>
    /// <param name="mode">What a failing check should do.</param>
    public BenzeneStartUpCheckOptions(BenzeneStartUpCheckMode mode = BenzeneStartUpCheckMode.Enforce)
    {
        Mode = mode;
    }

    /// <summary>What a failing check does.</summary>
    public BenzeneStartUpCheckMode Mode { get; }
}
