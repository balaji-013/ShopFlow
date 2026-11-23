using ShopFlow.Domain.Common;

namespace ShopFlow.Domain.Entities;

public enum OrderStatus { Pending = 0, Paid = 1, Shipped = 2, Delivered = 3, Cancelled = 4 }

public class Order : Entity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public OrderStatus Status { get; private set; } = OrderStatus.Pending;
    public decimal Total { get; private set; }
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;
    public DateTime? PaidAt { get; private set; }
    public string? PaymentReference { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public List<OrderItem> Items { get; private set; } = [];

    private Order() { }

    /// <summary>Aggregate root factory: an order can never exist without items, and Total is always derived, never trusted from the client.</summary>
    public static Order Create(Guid tenantId, Guid userId, IEnumerable<(Guid ProductId, string Name, decimal UnitPrice, int Quantity)> lines)
    {
        var order = new Order { TenantId = tenantId, UserId = userId };
        foreach (var l in lines)
        {
            if (l.Quantity <= 0) throw new DomainException("Quantity must be positive.", "invalid_quantity");
            order.Items.Add(new OrderItem(tenantId, order.Id, l.ProductId, l.Name, l.UnitPrice, l.Quantity));
        }
        if (order.Items.Count == 0) throw new DomainException("An order needs at least one item.", "empty_order");
        order.Total = order.Items.Sum(i => i.UnitPrice * i.Quantity);
        return order;
    }

    // State machine: the legal transitions live in ONE place.
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Transitions = new()
    {
        [OrderStatus.Pending]   = [OrderStatus.Paid, OrderStatus.Cancelled],
        [OrderStatus.Paid]      = [OrderStatus.Shipped],
        [OrderStatus.Shipped]   = [OrderStatus.Delivered],
        [OrderStatus.Delivered] = [],
        [OrderStatus.Cancelled] = [],
    };

    public bool CanMoveTo(OrderStatus next) => Transitions[Status].Contains(next);

    private void MoveTo(OrderStatus next)
    {
        if (!CanMoveTo(next))
            throw new DomainException($"Cannot move order from {Status} to {next}.", "invalid_transition");
        Status = next;
    }

    public void MarkPaid(string paymentReference)
    {
        MoveTo(OrderStatus.Paid); PaidAt = DateTime.UtcNow; PaymentReference = paymentReference;
    }
    public void Ship() => MoveTo(OrderStatus.Shipped);
    public void Deliver() => MoveTo(OrderStatus.Delivered);
    public void Cancel() => MoveTo(OrderStatus.Cancelled);
}

public class OrderItem : Entity, ITenantOwned
{
    public Guid TenantId { get; private set; }   // denormalised on purpose: lets the tenant filter / row-level security cover child rows too
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = default!;   // snapshot: the price/name at purchase time must not change retroactively
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }

    private OrderItem() { }
    public OrderItem(Guid tenantId, Guid orderId, Guid productId, string name, decimal unitPrice, int quantity)
    { TenantId = tenantId; OrderId = orderId; ProductId = productId; ProductName = name; UnitPrice = unitPrice; Quantity = quantity; }
}

