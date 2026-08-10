namespace Benzene.Clients.InProcess;

/// <summary>
/// One target of an in-process fan-out: a named pipeline (registered via
/// <c>AddInProcessMessaging</c>) and the topic to dispatch under <em>within that pipeline</em>.
/// </summary>
/// <remarks>
/// The topic is per-target, not shared, because Benzene's (topic, version) → at most one handler
/// invariant is enforced process-wide - see <see cref="DuplicateInProcessFanOutTargetException"/> for
/// why two targets reacting to what is conceptually one event still need two different topic strings.
/// </remarks>
/// <param name="PipelineName">The in-process pipeline this target dispatches to.</param>
/// <param name="Topic">The topic to dispatch under, unique among every target in the same fan-out call.</param>
public record InProcessFanOutTarget(string PipelineName, string Topic);
