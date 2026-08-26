namespace Benzene.Cache.Core;

#nullable enable

public interface ICacheService
{
    Task<bool> CanConnectAsync(CancellationToken cancellationToken = default);
}
