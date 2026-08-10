namespace Benzene.Clients.InProcess;

/// <summary>
/// Records that some outbound route called <c>.UseInProcess(name)</c> for <see cref="PipelineName"/>.
/// Registered once per <c>.UseInProcess(...)</c> call (as a multi-registered collection service, via
/// <see cref="Abstractions.DI.IBenzeneServiceContainer.AddSingleton{T}(T)"/> - resolved with
/// <see cref="Abstractions.DI.IServiceResolver.GetServices{T}"/>, not <c>GetService</c>), so
/// <see cref="InProcessRouteStartUpCheck"/> can see every referenced name without reflecting into
/// the built outbound routing table.
/// </summary>
/// <param name="PipelineName">The pipeline name the route was configured to dispatch to.</param>
public record InProcessRouteReference(string PipelineName);
