using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RefactorOrders.Data;
using RefactorOrders.Models;

namespace RefactorOrders.Tests;

public class OrderApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OrderApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ApplicationDbContext));
                if (descriptor is not null)
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseInMemoryDatabase("IntegrationTestsDb"));
            });
        });
    }

    [Fact]
    public async Task PostApiOrders_ReturnsCreatedAndResponseShape()
    {
        using var client = _factory.CreateClient();

        var request = new OrderRequest
        {
            CustomerName = "VIP Customer",
            CustomerNotes = "Priority order",
            CouponCode = "SAVE10",
            ExpressShipping = true,
            Items = new List<OrderItemRequest>
            {
                new() { Sku = "SKU-1", Description = "Widget", UnitPrice = 100m, Quantity = 1 }
            }
        };

        var response = await client.PostAsJsonAsync("/api/order", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<OrderCreateResponse>();

        Assert.NotNull(body);
        Assert.True(body!.Success);
        Assert.Equal("Order accepted", body.Message);
        Assert.Equal("VIP Customer", body.CustomerName);
        Assert.Equal("P2", body.PriorityCode);
        Assert.True(body.ExpressShipping);
        Assert.NotNull(body.OrderNumber);
        Assert.NotNull(body.Summary);
    }
}
