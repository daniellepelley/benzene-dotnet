using Benzene.Example.Outbox.Domain;
using Benzene.Outbox;

namespace Benzene.Example.Outbox.Store;

/// <summary>
/// A minimal stand-in for the "outbox-aware unit of work" <c>Benzene.Outbox.DynamoDb</c>/
/// <c>Benzene.Outbox.EntityFramework</c> ship for real (see <see cref="OutboxWriteMode.Transactional"/>'s
/// xmldoc): it drains whatever <see cref="OutboxMiddleware"/> staged into <see cref="BufferedOutboxStage"/>
/// and persists it alongside the order write, in one place, so both happen or neither does.
///
/// <b>This is illustrative, not a real transaction.</b> The two writes below (<see cref="Orders.Add"/>
/// and <see cref="IOutboxStore.AddAsync"/>) are still two separate in-memory operations — there's no
/// database here to make them atomic. A real deployment needs an actual transactional store
/// (<c>Benzene.Outbox.DynamoDb</c>'s <c>TransactWriteItems</c>, or <c>Benzene.Outbox.EntityFramework</c>'s
/// same-<c>DbContext</c> + one <c>SaveChangesAsync</c>) to make that guarantee real. What this class does
/// demonstrate correctly: the *shape* of the integration — drain the stage, commit it together with the
/// state write — and that a scope which never commits discards its staged envelope, exactly like the
/// real store packages do.
/// </summary>
public sealed class TransactionalOrders(Orders orders, BufferedOutboxStage stage, IOutboxStore store)
{
    /// <summary>Commits the order and every envelope staged on this scope together.</summary>
    public async Task CommitAsync(Order order)
    {
        orders.Add(order);
        await store.AddAsync(stage.DrainStaged());
    }
}
