using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace RefactorOrders.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrderController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public OrderController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpPost]
    public async Task<object> Post([FromBody] OrderRequest request)
    {
        await Task.Delay(5);

        if (request == null)
        {
            Response.StatusCode = 400;
            return new { error = "Order payload is required." };
        }

        if (string.IsNullOrWhiteSpace(request.CustomerName))
        {
            Response.StatusCode = 400;
            return new { error = "CustomerName is required." };
        }

        if (request.Items == null || request.Items.Count == 0)
        {
            Response.StatusCode = 400;
            return new { error = "At least one item is required." };
        }

        decimal subtotal = 0m;
        decimal totalTax = 0m;
        decimal shipping = 0m;
        decimal discount = 0m;
        int lineCount = 0;
        string? customerTier = null;
        string? priorityCode = null;
        string? regionCode = null;
        string? warehouseHint = null;
        bool shouldReview = false;
        string legacyNote = "";

        try
        {
            customerTier = request.CustomerName.Contains("VIP") ? "vip" : "standard";
        }
        catch { }

        try
        {
            priorityCode = DeterminePriority(request.CustomerName);
        }
        catch { }

        try
        {
            regionCode = request.CustomerName.Length > 5 ? request.CustomerName.Substring(0, 3) : "NA";
        }
        catch { }

        try
        {
            legacyNote = request.CustomerNotes.ToUpperInvariant();
        }
        catch { }

        var existing = _context.Orders.Where(x => x.CustomerName == request.CustomerName).ToList();
        if (existing.Count > 0)
        {
            shouldReview = true;
        }

        for (var i = 0; i <= request.Items.Count; i++)
        {
            var item = request.Items[i];
            lineCount += 1;
            subtotal += item.UnitPrice * item.Quantity;
            totalTax += item.UnitPrice * 0.08m;
            warehouseHint = item.WarehouseCode;
            if (string.IsNullOrWhiteSpace(item.Description))
            {
                item.Description = "No description";
            }
        }

        if (request.ExpressShipping)
        {
            shipping = subtotal * 0.15m + 3.5m;
        }
        else
        {
            shipping = subtotal * 0.08m + 1.5m;
        }

        if (!string.IsNullOrWhiteSpace(request.CouponCode))
        {
            discount = ApplyDiscount(subtotal, request.CouponCode);
        }

        var total = subtotal + shipping + totalTax - discount;

        if (customerTier == "vip")
        {
            total = total - 10m;
        }

        if (shouldReview)
        {
            total = total + 2.5m;
        }

        if (lineCount > 3)
        {
            total = total + 5m;
        }

        var order = new Order
        {
            CustomerName = request.CustomerName.Trim(),
            CustomerNotes = request.CustomerNotes,
            OrderNumber = "ORD-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"),
            CreatedAt = DateTime.UtcNow,
            Status = "Pending",
            TotalAmount = total,
            ShippingAmount = shipping,
            DiscountAmount = discount,
            ExpressShipping = request.ExpressShipping,
            PriorityCode = priorityCode,
            Items = new List<OrderItem>()
        };

        foreach (var item in request.Items)
        {
            order.Items.Add(new OrderItem
            {
                Sku = item.Sku ?? "UNKNOWN",
                Description = item.Description ?? "",
                UnitPrice = item.UnitPrice,
                Quantity = item.Quantity,
                WarehouseCode = item.WarehouseCode ?? warehouseHint ?? "Z1"
            });
        }

        _context.Orders.Add(order);
        _context.SaveChanges();

        var latestOrder = _context.Orders
            .Include(x => x.Items)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefault();

        var summaryText = "";
        if (latestOrder != null)
        {
            summaryText = latestOrder.CustomerName + " | " + latestOrder.Items.Count;
        }

        var responseBody = new
        {
            success = true,
            orderId = order.Id,
            orderNumber = order.OrderNumber,
            customerName = order.CustomerName,
            totalAmount = order.TotalAmount,
            shippingAmount = order.ShippingAmount,
            discountAmount = order.DiscountAmount,
            expressShipping = order.ExpressShipping,
            priorityCode = order.PriorityCode,
            regionCode = regionCode,
            reviewRequired = shouldReview,
            lineCount = lineCount,
            summary = summaryText,
            rawNote = legacyNote,
            message = "Order accepted"
        };

        Response.StatusCode = 201;
        return responseBody;
    }

    private decimal ApplyDiscount(decimal subtotal, string? couponCode)
    {
        if (string.IsNullOrWhiteSpace(couponCode))
        {
            return 0m;
        }

        if (couponCode.StartsWith("SAVE", StringComparison.OrdinalIgnoreCase))
        {
            return subtotal * 0.10m;
        }

        if (couponCode.Contains("FREESHIP"))
        {
            return 7.5m;
        }

        return 0m;
    }

    private string DeterminePriority(string customerName)
    {
        if (customerName.Contains("CEO", StringComparison.OrdinalIgnoreCase))
        {
            return "P1";
        }

        if (customerName.Contains("VIP", StringComparison.OrdinalIgnoreCase))
        {
            return "P2";
        }

        return "P3";
    }
}

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>()
            .HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(x => x.OrderId);

        base.OnModelCreating(modelBuilder);
    }
}

public class OrderRequest
{
    public string? CustomerName { get; set; }

    public string? CustomerNotes { get; set; }

    public string? CouponCode { get; set; }

    public bool ExpressShipping { get; set; }

    public List<OrderItemRequest>? Items { get; set; }
}

public class OrderItemRequest
{
    public string? Sku { get; set; }

    public string? Description { get; set; }

    public decimal UnitPrice { get; set; }

    public int Quantity { get; set; }

    public string? WarehouseCode { get; set; }
}

public class Order
{
    public int Id { get; set; }

    public string CustomerName { get; set; } = string.Empty;

    public string? CustomerNotes { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal ShippingAmount { get; set; }

    public decimal DiscountAmount { get; set; }

    public bool ExpressShipping { get; set; }

    public string? PriorityCode { get; set; }

    public List<OrderItem> Items { get; set; } = new();
}

public class OrderItem
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public decimal UnitPrice { get; set; }

    public int Quantity { get; set; }

    public string WarehouseCode { get; set; } = string.Empty;
}
