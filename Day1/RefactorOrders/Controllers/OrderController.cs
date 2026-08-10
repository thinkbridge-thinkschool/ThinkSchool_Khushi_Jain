using Microsoft.AspNetCore.Mvc;
using RefactorOrders.Models;
using RefactorOrders.Services;

namespace RefactorOrders.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrderController : ControllerBase
{
    private readonly IOrderService _orderService;
    private readonly ILogger<OrderController> _logger;

    public OrderController(IOrderService orderService, ILogger<OrderController> logger)
    {
        _orderService = orderService;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<OrderCreateResponse>> Post([FromBody] OrderRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(5, cancellationToken);

        var result = await _orderService.CreateOrderAsync(request, cancellationToken);

        if (!result.IsSuccess || result.Response is null)
        {
            _logger.LogWarning("Order creation failed: {ErrorMessage}", result.ErrorMessage);
            Response.StatusCode = 400;
            return BadRequest(result.Response ?? new OrderCreateResponse { Success = false, Error = result.ErrorMessage ?? "Order could not be processed." });
        }

        Response.StatusCode = 201;
        return Created(string.Empty, result.Response);
    }
}
