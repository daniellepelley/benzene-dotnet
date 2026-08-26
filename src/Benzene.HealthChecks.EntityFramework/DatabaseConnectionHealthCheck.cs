using Benzene.HealthChecks.Core;
using Microsoft.EntityFrameworkCore;

namespace Benzene.HealthChecks.EntityFramework;

/// <summary>
/// Checks only that <typeparamref name="TDbContext"/> can connect - unlike
/// <see cref="DatabaseHealthCheck{TDbContext}"/>, it does not also verify the applied migration.
/// </summary>
/// <typeparam name="TDbContext">The EF Core context type to check.</typeparam>
public class DatabaseConnectionHealthCheck<TDbContext> : IHealthCheck where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;

    /// <inheritdoc />
    public string Type => "DatabaseConnection";

    /// <summary>Initializes a new instance of the <see cref="DatabaseConnectionHealthCheck{TDbContext}"/> class.</summary>
    /// <param name="dbContext">The context to check.</param>
    public DatabaseConnectionHealthCheck(TDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Checks connectivity via <c>DbContext.Database.CanConnectAsync</c>. The result's
    /// <see cref="IHealthCheckResult.Data"/> includes <c>CanConnect</c> and <c>Error</c> (the
    /// connection exception's type name, if any - not its message, since some ADO.NET providers
    /// include connection details such as server/credentials in exception messages, and this result
    /// can flow out to whatever calls the health check topic with no built-in authorization).
    /// </summary>
    public async Task<IHealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var canConnect = await TryConnect(_dbContext, cancellationToken);

        return HealthCheckResult.CreateInstance(canConnect.Item1, Type, new Dictionary<string, object>
        {
            { "CanConnect", canConnect.Item1 },
            { "Error", canConnect.Item2?.GetType().Name }
        },
        new[] { new HealthCheckDependency("Database", typeof(TDbContext).Name) });
    }

    private static async Task<(bool, Exception)> TryConnect(DbContext dbContext, CancellationToken cancellationToken)
    {
        try
        {
            return (await dbContext.Database.CanConnectAsync(cancellationToken), null);
        }
        catch (OperationCanceledException)
        {
            // A caller-driven cancellation (ambient token / the processor's own per-check timeout) is not
            // a connectivity failure - propagate uncaught so ExceptionHandlingHealthCheck (which every
            // check runs under via HealthCheckProcessor) classifies it as the distinct "Cancelled" outcome
            // instead of recording "the database is broken" for a graceful shutdown or timeout.
            throw;
        }
        catch(Exception ex)
        {
            return (false, ex);
        }
    }
}
