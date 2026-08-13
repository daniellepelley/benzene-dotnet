using Benzene.Outbox.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Benzene.Test.Outbox.EntityFramework;

/// <summary>
/// A minimal application <see cref="DbContext"/> for the EF Core outbox tests: one application
/// entity (<see cref="TestOrder"/>, standing in for "the handler's own state write") alongside the
/// outbox's own <c>OutboxRecord</c> mapping - exactly the shape a real application's context takes.
/// </summary>
public class TestOutboxDbContext : DbContext
{
    public TestOutboxDbContext(DbContextOptions<TestOutboxDbContext> options)
        : base(options)
    {
    }

    public DbSet<TestOrder> Orders => Set<TestOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxEntities();
        modelBuilder.Entity<TestOrder>(entity =>
        {
            entity.HasKey(o => o.Id);
        });
    }
}

/// <summary>Stands in for the application's own state write, alongside the staged outbox row, in the same <c>SaveChangesAsync</c>.</summary>
public class TestOrder
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>
/// A trivial <see cref="IDbContextFactory{TContext}"/> for tests - wraps a fixed
/// <see cref="DbContextOptions{TContext}"/> (typically an EF Core InMemory database name), so every
/// <see cref="CreateDbContext"/> call opens a fresh context against the same underlying database,
/// exactly like <c>Microsoft.Extensions.DependencyInjection</c>'s own <c>AddDbContextFactory</c>
/// would produce.
/// </summary>
public class TestOutboxDbContextFactory : IDbContextFactory<TestOutboxDbContext>
{
    private readonly DbContextOptions<TestOutboxDbContext> _options;

    public TestOutboxDbContextFactory(DbContextOptions<TestOutboxDbContext> options)
    {
        _options = options;
    }

    public TestOutboxDbContext CreateDbContext() => new(_options);
}
