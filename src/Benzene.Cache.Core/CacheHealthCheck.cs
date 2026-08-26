using Microsoft.Extensions.Logging;
using Benzene.HealthChecks.Core;

namespace Benzene.Cache.Core;

public class CacheHealthCheck<TCacheService> : IHealthCheck where TCacheService : ICacheService
{
    private readonly ILogger<CacheHealthCheck<TCacheService>> _logger;
    private readonly TCacheService _cacheService;

    public string Type => "Cache";

    public CacheHealthCheck(TCacheService cacheService, ILogger<CacheHealthCheck<TCacheService>> logger)
    {
        _cacheService = cacheService;
        _logger = logger;
    }

    // ICacheService.CanConnectAsync() has no CancellationToken overload (out of WP-7's scope - only
    // IHealthCheck's own contract changed here), so the token cannot be forwarded into it.
    public async Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var dependencies = new[] { new HealthCheckDependency("Cache", typeof(TCacheService).Name) };

        try
        {
            var canConnect = await _cacheService.CanConnectAsync();

            return HealthCheckResult.CreateInstance(canConnect, Type, new Dictionary<string, object>
            {
                { "CanConnect", canConnect },
            }, dependencies);
        }
        catch (OperationCanceledException)
        {
            // A caller-driven cancellation (ambient token / the processor's own per-check timeout) is not
            // a connectivity failure - propagate uncaught so ExceptionHandlingHealthCheck (which every
            // check runs under via HealthCheckProcessor) classifies it as the distinct "Cancelled" outcome
            // instead of an ordinary reachability failure.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in cache health check");
            return HealthCheckResult.CreateInstance(false, Type, new Dictionary<string, object>
            {
                { "CanConnect", false },
                { "Error", ex.GetType().Name }
            }, dependencies);
        }
    }
}
