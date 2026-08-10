using Microsoft.Extensions.Logging;
using Moq;
using RefactorOrders.Models;
using RefactorOrders.Repositories;
using RefactorOrders.Services;

namespace RefactorOrders.Tests;

public class OrderServiceTests
{
    [Fact]
    public async Task CreateOrderAsync_ReturnsValidationError_WhenCustomerNameMissing()
    {
        var repository = new Mock<IOrderRepository>();
        var logger = Mock.Of<ILogger<OrderService>>();
        var service = new OrderService(repository.Object, logger);

        var result = await service.CreateOrderAsync(new OrderRequest
        {
            CustomerName = "   ",
            Items = new List<OrderItemRequest> { new() { UnitPrice = 10m, Quantity = 1 } }
        }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Response);
        Assert.False(result.Response!.Success);
        Assert.Equal("CustomerName is required.", result.Response.Error);
        repository.Verify(x => x.HasOrderForCustomerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateOrderAsync_AppliesVipDiscountAndPriorityRule()
    {
        var repository = new Mock<IOrderRepository>();
        repository.Setup(x => x.HasOrderForCustomerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        repository.Setup(x => x.CreateOrderAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        repository.Setup(x => x.GetLatestOrderAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Order?)null);

        var logger = Mock.Of<ILogger<OrderService>>();
        var service = new OrderService(repository.Object, logger);

        var result = await service.CreateOrderAsync(new OrderRequest
        {
            CustomerName = "VIP CEO",
            CouponCode = "SAVE10",
            ExpressShipping = true,
            Items = new List<OrderItemRequest>
            {
                new() { UnitPrice = 100m, Quantity = 1, Description = "Widget" }
            }
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Response);
        Assert.True(result.Response!.Success);
        Assert.Equal("P1", result.Response.PriorityCode);
        Assert.Equal(100m + 18.5m + 8m - 10m - 10m, result.Response.TotalAmount);
    }

    [Fact]
    public async Task CreateOrderAsync_HandlesNullNotesAndUsesSafeLoopBehavior()
    {
        var repository = new Mock<IOrderRepository>();
        repository.Setup(x => x.HasOrderForCustomerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        repository.Setup(x => x.CreateOrderAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        repository.Setup(x => x.GetLatestOrderAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Order?)null);

        var logger = Mock.Of<ILogger<OrderService>>();
        var service = new OrderService(repository.Object, logger);

        var items = new List<OrderItemRequest>
        {
            new() { UnitPrice = 10m, Quantity = 2, Description = null },
            new() { UnitPrice = 5m, Quantity = 1, Description = "Capsule" }
        };

        var result = await service.CreateOrderAsync(new OrderRequest
        {
            CustomerName = "Alice",
            CustomerNotes = null,
            Items = items
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Response);
        Assert.Equal(string.Empty, result.Response!.RawNote);
        Assert.Equal(2, result.Response.LineCount);
        Assert.Equal("No description", items[0].Description);
    }
}
