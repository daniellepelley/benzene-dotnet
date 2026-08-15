namespace Benzene.Example.Outbox.Domain;

/// <summary>The "business data" the handler writes — the half of the atomic commit that isn't the event.</summary>
public sealed class Order
{
    public required Guid Id { get; init; }
    public required string Customer { get; init; }
    public required decimal Total { get; init; }
}

/// <summary>Both the create request and the <c>order:created</c> event payload (CRUD-convention response-as-event).</summary>
public sealed class CreateOrderRequest
{
    public required Guid Id { get; init; }
    public required string Customer { get; init; }
    public required decimal Total { get; init; }
}
