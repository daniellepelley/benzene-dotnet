using Microsoft.EntityFrameworkCore;

namespace Benzene.Outbox.EntityFramework;

/// <summary>
/// Adds the outbox's <see cref="OutboxRecord"/> mapping to an application's own
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/> - call from the application's own
/// <c>OnModelCreating</c>.
/// </summary>
public static class ModelBuilderExtensions
{
    /// <summary>The table name used when <c>AddOutboxEntities</c> is called with no explicit override.</summary>
    public const string DefaultTableName = "BenzeneOutbox";

    /// <summary>
    /// Maps <see cref="OutboxRecord"/> onto <paramref name="tableName"/> (default
    /// <c>"BenzeneOutbox"</c>), with an index on <c>(Status, NextAttemptAtUtc)</c> for the sweep
    /// query (<see cref="IOutboxStore.ClaimDueAsync"/>).
    /// </summary>
    /// <param name="modelBuilder">The application's model builder, from its own <c>OnModelCreating</c>.</param>
    /// <param name="tableName">The table to map <see cref="OutboxRecord"/> onto. Defaults to <see cref="DefaultTableName"/>.</param>
    /// <example>
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    /// {
    ///     modelBuilder.AddOutboxEntities();
    ///     // ... the application's own entities ...
    /// }
    /// </code>
    /// </example>
    public static ModelBuilder AddOutboxEntities(this ModelBuilder modelBuilder, string tableName = DefaultTableName)
    {
        modelBuilder.Entity<OutboxRecord>(entity =>
        {
            entity.ToTable(tableName);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasMaxLength(64);
            entity.Property(r => r.Topic).IsRequired();
            entity.Property(r => r.Payload).IsRequired();
            entity.Property(r => r.PayloadType).IsRequired();
            entity.Property(r => r.HeadersJson).IsRequired();

            // A hand-rolled concurrency token (not a database-generated rowversion/timestamp column)
            // so it works identically across every relational provider; only the optimistic-concurrency
            // fallback claim path in EntityFrameworkOutboxStore<TDbContext> consults it - see its remarks.
            entity.Property(r => r.RowVersion).IsConcurrencyToken();

            entity.HasIndex(r => new { r.Status, r.NextAttemptAtUtc })
                .HasDatabaseName($"IX_{tableName}_Status_NextAttemptAtUtc");
        });

        return modelBuilder;
    }
}
