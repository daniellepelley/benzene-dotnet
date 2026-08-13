using Benzene.Abstractions.DI;
using Microsoft.EntityFrameworkCore;

namespace Benzene.Outbox.EntityFramework;

/// <summary>
/// Registration helpers for the EF Core-backed outbox: registers the process-wide engine (same as
/// <see cref="Extensions.AddOutbox"/>) plus <see cref="EntityFrameworkOutboxStore{TDbContext}"/> and
/// <see cref="EntityFrameworkOutboxStage{TDbContext}"/>.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Registers the outbox engine (equivalent to calling <c>AddOutbox(configure)</c> - safe to call
    /// even if you already have, since <c>AddOutbox</c>'s own <see cref="OutboxOptions"/> registration
    /// is idempotent and whichever call registered it first wins) plus the EF Core store and stage for
    /// <typeparamref name="TDbContext"/>, superseding <c>AddInMemoryOutboxStore</c>'s default
    /// <see cref="IOutboxStore"/> and <c>AddOutbox</c>'s default <see cref="IOutboxStage"/>
    /// (<see cref="BufferedOutboxStage"/>) if either was already registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires TWO EF Core registrations against <typeparamref name="TDbContext"/> in the host, with
    /// the same connection/options - not one:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>services.AddDbContext&lt;TDbContext&gt;(options)</c> - the normal scoped registration your
    /// handlers already use, and the one <see cref="EntityFrameworkOutboxStage{TDbContext}"/> resolves
    /// so it shares the handler's own <c>DbContext</c> instance (the atomicity story).
    /// </description></item>
    /// <item><description>
    /// <c>services.AddDbContextFactory&lt;TDbContext&gt;(options)</c> - <see cref="EntityFrameworkOutboxStore{TDbContext}"/>
    /// resolves <c>IDbContextFactory&lt;TDbContext&gt;</c> instead of a scoped context, because it is
    /// registered singleton and held for the process's lifetime by Phase 1's <c>OutboxDispatcher</c>
    /// (see <see cref="EntityFrameworkOutboxStore{TDbContext}"/>'s remarks for why a directly injected
    /// scoped context would be unsafe there).
    /// </description></item>
    /// </list>
    /// <para>Omitting the factory registration surfaces as a DI resolution failure the first time the store is used - see the package's CLAUDE.md for the full wiring example.</para>
    /// </remarks>
    /// <typeparam name="TDbContext">The application's own <see cref="DbContext"/> type, mapped via <see cref="ModelBuilderExtensions.AddOutboxEntities"/>.</typeparam>
    /// <param name="services">The service container.</param>
    /// <param name="configure">Forwarded to <c>AddOutbox</c> - configures the shared <see cref="OutboxOptions"/> the dispatch engine and every route's default capture behavior read.</param>
    /// <param name="now">A clock for <see cref="EntityFrameworkOutboxStore{TDbContext}"/>, overridable for tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static IBenzeneServiceContainer AddEntityFrameworkOutbox<TDbContext>(
        this IBenzeneServiceContainer services,
        Action<OutboxOptions>? configure = null,
        Func<DateTimeOffset>? now = null)
        where TDbContext : DbContext
    {
        services.AddOutbox(configure);

        services.AddSingleton<IOutboxStore>(resolver =>
            new EntityFrameworkOutboxStore<TDbContext>(resolver.GetService<IDbContextFactory<TDbContext>>(), now));
        services.AddScoped<IOutboxStage, EntityFrameworkOutboxStage<TDbContext>>();

        return services;
    }
}
