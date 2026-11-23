using FluentAssertions;
using ShopFlow.Domain.Common;
using ShopFlow.Domain.Entities;

namespace ShopFlow.UnitTests;

public class OrderTests
{
    private static Order NewOrder(int qty = 2, decimal price = 10m) =>
        Order.Create(Guid.NewGuid(), Guid.NewGuid(), [(Guid.NewGuid(), "Widget", price, qty)]);

    [Fact]
    public void Total_is_derived_from_lines_never_supplied_by_caller()
    {
        var o = Order.Create(Guid.NewGuid(), Guid.NewGuid(), [(Guid.NewGuid(), "A", 10.50m, 2), (Guid.NewGuid(), "B", 3m, 1)]);
        o.Total.Should().Be(24.00m);
        o.Status.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public void Empty_order_is_rejected() =>
        FluentActions.Invoking(() => Order.Create(Guid.NewGuid(), Guid.NewGuid(), [])).Should().Throw<DomainException>().Which.Code.Should().Be("empty_order");

    [Theory, InlineData(0), InlineData(-1)]
    public void Non_positive_quantity_is_rejected(int qty) =>
        FluentActions.Invoking(() => NewOrder(qty)).Should().Throw<DomainException>();

    [Fact]
    public void Happy_path_walks_the_state_machine()
    {
        var o = NewOrder();
        o.MarkPaid("pi_1"); o.Ship(); o.Deliver();
        o.Status.Should().Be(OrderStatus.Delivered);
        o.PaymentReference.Should().Be("pi_1");
    }

    [Fact]
    public void Cannot_ship_an_unpaid_order() =>
        FluentActions.Invoking(() => NewOrder().Ship()).Should().Throw<DomainException>().Which.Code.Should().Be("invalid_transition");

    [Fact]
    public void Cannot_cancel_after_payment()
    {
        var o = NewOrder(); o.MarkPaid("pi_1");
        FluentActions.Invoking(() => o.Cancel()).Should().Throw<DomainException>();
    }

    [Fact]
    public void Cancelled_is_terminal()
    {
        var o = NewOrder(); o.Cancel();
        o.CanMoveTo(OrderStatus.Paid).Should().BeFalse();
    }
}

public class ProductTests
{
    [Fact]
    public void Negative_price_or_stock_is_rejected()
    {
        FluentActions.Invoking(() => new Product(Guid.NewGuid(), Guid.NewGuid(), "S", "N", "", -1m, 1)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => new Product(Guid.NewGuid(), Guid.NewGuid(), "S", "N", "", 1m, -1)).Should().Throw<DomainException>();
    }
}
