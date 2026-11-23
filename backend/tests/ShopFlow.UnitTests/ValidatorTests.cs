using FluentAssertions;
using ShopFlow.Application.Orders;

namespace ShopFlow.UnitTests;

public class PlaceOrderValidatorTests
{
    private readonly PlaceOrderValidator _v = new();
    private static PlaceOrderRequest Req(params int[] qtys) => new(qtys.Select(q => new OrderLineRequest(Guid.NewGuid(), q)).ToList());

    [Fact] public void Valid_request_passes() => _v.Validate(Req(1, 5)).IsValid.Should().BeTrue();
    [Fact] public void Empty_fails() => _v.Validate(Req()).IsValid.Should().BeFalse();
    [Theory, InlineData(0), InlineData(-3), InlineData(1001)]
    public void Out_of_range_quantity_fails(int q) => _v.Validate(Req(q)).IsValid.Should().BeFalse();
    [Fact] public void More_than_50_lines_fails() => _v.Validate(Req(Enumerable.Repeat(1, 51).ToArray())).IsValid.Should().BeFalse();
}
