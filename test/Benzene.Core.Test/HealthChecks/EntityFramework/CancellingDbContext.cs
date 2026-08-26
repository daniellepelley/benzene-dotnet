using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Benzene.Test.HealthChecks.EntityFramework;

// Test doubles for #114's cancellation-propagation tests. DatabaseFacade.CanConnectAsync and
// DbContext.Database are both virtual specifically to support this style of test double (EF Core's own
// documented mocking pattern) - the EF Core InMemory provider's real CanConnectAsync does not observe an
// already-cancelled token at all (confirmed: it completes normally instead of throwing), so there is no
// way to reproduce the caller-driven-cancellation scenario through the real provider.
internal class CancellingDatabaseFacade : DatabaseFacade
{
    public CancellingDatabaseFacade(DbContext context) : base(context)
    {
    }

    public override Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
        => throw new OperationCanceledException();
}

internal class CancellingDbContext : TestDbContext
{
    public CancellingDbContext(DbContextOptions<TestDbContext> options) : base(options)
    {
    }

    public override DatabaseFacade Database => new CancellingDatabaseFacade(this);
}
