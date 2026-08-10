using Microsoft.Extensions.Logging;
using RefactorOrders.Models;
using RefactorOrders.Repositories;

namespace RefactorOrders.Services;

public class OrderService : IOrderService
{
    private readonly IOrderRepository _repository;
    private readonly ILogger<OrderService> _logger;
    private readonly IEnumerable<IOrderRule> _rules;

    public OrderService(IOrderRepository repository, ILogger<OrderService> logger, IEnumerable<IOrderRule> rules)
    {
        _repository = repository;
        _logger = logger;
        _rules = rules;
    }

    public async Task<OrderProcessingResult> CreateOrderAsync(OrderRequest request, CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return new OrderProcessingResult
            {
                IsSuccess = false,
                Response = new OrderCreateResponse
                {
                    Success = false,
                    Error = "Order payload is required."
                }
            };
        }

        if (string.IsNullOrWhiteSpace(request.CustomerName))
        {
            return new OrderProcessingResult
            {
                IsSuccess = false,
                Response = new OrderCreateResponse
                {
                    Success = false,
                    Error = "CustomerName is required."
                }
            };
        }

        if (request.Items == null || request.Items.Count == 0)
        {
            return new OrderProcessingResult
            {
                IsSuccess = false,
                Response = new OrderCreateResponse
                {
                    Success = false,
                    Error = "At least one item is required."
                }
            };
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
        string legacyNote = string.Empty;

        try
        {
            customerTier = request.CustomerName.Contains("VIP", StringComparison.OrdinalIgnoreCase) ? "vip" : "standard";
        }
        catch (Exception ex) when (ex is ArgumentNullException or ArgumentException)
        {
            _logger.LogWarning(ex, "Unable to determine customer tier from {CustomerName}", request.CustomerName);
        }

        try
        {
            priorityCode = DeterminePriority(request.CustomerName);
        }
        catch (Exception ex) when (ex is ArgumentNullException or ArgumentException)
        {
            _logger.LogWarning(ex, "Unable to determine priority from {CustomerName}", request.CustomerName);
            priorityCode = "P3";
        }

        try
        {
            regionCode = request.CustomerName.Length > 5 ? request.CustomerName.Substring(0, 3) : "NA";
        }
        catch (Exception ex) when (ex is ArgumentNullException or ArgumentException)
        {
            _logger.LogWarning(ex, "Unable to determine region from {CustomerName}", request.CustomerName);
            regionCode = "NA";
        }

        try
        {
            legacyNote = request.CustomerNotes?.ToUpperInvariant() ?? string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentNullException or ArgumentException)
        {
            _logger.LogWarning(ex, "Unable to normalize customer notes for {CustomerName}", request.CustomerName);
            legacyNote = string.Empty;
        }

        shouldReview = await _repository.HasOrderForCustomerAsync(request.CustomerName, cancellationToken);

        for (var i = 0; i < request.Items.Count; i++)
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

        var context = new OrderProcessingContext
        {
            CustomerName = request.CustomerName,
            CustomerNotes = request.CustomerNotes,
            CouponCode = request.CouponCode,
            ExpressShipping = request.ExpressShipping,
            Subtotal = subtotal,
            TotalTax = totalTax,
            Shipping = shipping,
            Discount = discount,
            Total = total,
            LineCount = lineCount,
            ShouldReview = shouldReview,
            CustomerTier = customerTier,
            PriorityCode = priorityCode,
            RegionCode = regionCode,
            WarehouseHint = warehouseHint,
            LegacyNote = legacyNote
        };

        foreach (var rule in _rules)
        {
            rule.Apply(context);
        }

        total = context.Total;

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
                Description = item.Description ?? string.Empty,
                UnitPrice = item.UnitPrice,
                Quantity = item.Quantity,
                WarehouseCode = item.WarehouseCode ?? warehouseHint ?? "Z1"
            });
        }

        await _repository.CreateOrderAsync(order, cancellationToken);

        var latestOrder = await _repository.GetLatestOrderAsync(cancellationToken);
        var summaryText = latestOrder is null
            ? string.Empty
            : latestOrder.CustomerName + " | " + latestOrder.Items.Count;

        var response = new OrderCreateResponse
        {
            Success = true,
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            CustomerName = order.CustomerName,
            TotalAmount = order.TotalAmount,
            ShippingAmount = order.ShippingAmount,
            DiscountAmount = order.DiscountAmount,
            ExpressShipping = order.ExpressShipping,
            PriorityCode = order.PriorityCode,
            RegionCode = regionCode,
            ReviewRequired = shouldReview,
            LineCount = lineCount,
            Summary = summaryText,
            RawNote = legacyNote,
            Message = "Order accepted"
        };

        _logger.LogInformation("Order {OrderNumber} accepted for {CustomerName}", order.OrderNumber, order.CustomerName);

        return new OrderProcessingResult
        {
            IsSuccess = true,
            Response = response
        };
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

        if (couponCode.Contains("FREESHIP", StringComparison.OrdinalIgnoreCase))
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
