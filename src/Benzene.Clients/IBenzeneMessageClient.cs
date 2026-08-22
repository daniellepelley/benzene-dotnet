using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Abstractions.Results;

namespace Benzene.Clients;

/// <summary>A transport-specific outbound client: sends one Benzene client request and returns its result.</summary>
public interface IBenzeneMessageClient : IDisposable
{
    /// <summary>Sends <paramref name="request"/> and returns the resulting <see cref="IBenzeneResult{T}"/>.</summary>
    Task<IBenzeneResult<TResponse>> SendMessageAsync<TRequest, TResponse>(IBenzeneClientRequest<TRequest> request);
}

/// <summary>Extension point for <see cref="IBenzeneMessageClient"/> — currently empty.</summary>
public static class BenzeneMessageClientExtensions
{
}