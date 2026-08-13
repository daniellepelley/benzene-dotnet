using System.Linq;
using Benzene.Outbox.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Benzene.Test.Outbox.EntityFramework;

public class ModelBuilderExtensionsTest
{
    [Fact]
    public void AddOutboxEntities_DefaultTableName_IsBenzeneOutbox()
    {
        var options = new DbContextOptionsBuilder<TestOutboxDbContext>()
            .UseInMemoryDatabase(nameof(AddOutboxEntities_DefaultTableName_IsBenzeneOutbox))
            .Options;
        using var dbContext = new TestOutboxDbContext(options);

        var entityType = dbContext.Model.FindEntityType(typeof(OutboxRecord));

        Assert.NotNull(entityType);
        Assert.Equal(ModelBuilderExtensions.DefaultTableName, entityType!.GetTableName());
    }

    [Fact]
    public void AddOutboxEntities_HasIndex_OnStatusAndNextAttemptAtUtc()
    {
        var options = new DbContextOptionsBuilder<TestOutboxDbContext>()
            .UseInMemoryDatabase(nameof(AddOutboxEntities_HasIndex_OnStatusAndNextAttemptAtUtc))
            .Options;
        using var dbContext = new TestOutboxDbContext(options);

        var entityType = dbContext.Model.FindEntityType(typeof(OutboxRecord))!;
        var index = Assert.Single(entityType.GetIndexes());

        Assert.Equal(new[] { nameof(OutboxRecord.Status), nameof(OutboxRecord.NextAttemptAtUtc) }, index.Properties.Select(p => p.Name).ToArray());
    }

    private class CustomTableDbContext : DbContext
    {
        public CustomTableDbContext(DbContextOptions<CustomTableDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddOutboxEntities("CustomOutboxTable");
        }
    }

    [Fact]
    public void AddOutboxEntities_TableNameIsOverridable()
    {
        var options = new DbContextOptionsBuilder<CustomTableDbContext>()
            .UseInMemoryDatabase(nameof(AddOutboxEntities_TableNameIsOverridable))
            .Options;
        using var dbContext = new CustomTableDbContext(options);

        var entityType = dbContext.Model.FindEntityType(typeof(OutboxRecord));

        Assert.Equal("CustomOutboxTable", entityType!.GetTableName());
    }
}
