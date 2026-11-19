using MarkUp.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarkUp.Api;

public sealed class BillingService
{
    private readonly SheetStore _store;
    private readonly MasterData _masters;
    private readonly AppClock _clock;

    public BillingService(SheetStore store, MasterData masters, AppClock clock)
    {
        _store = store;
        _masters = masters;
        _clock = clock;
    }

    /// <summary>Today's Order panel: every order placed today, any status.</summary>
    public Task<List<OrderView>> TodayAsync()
    {
        var today = _clock.Today;
        return LoadAsync(o => o.Date == today);
    }

    /// <summary>Old Pending Orders panel: earlier orders not yet billed (includes cancelled ones).</summary>
    public Task<List<OrderView>> PendingAsync()
    {
        var today = _clock.Today;
        return LoadAsync(o => o.Date < today && o.Status != OrderStatus.Billed);
    }

    private async Task<List<OrderView>> LoadAsync(Func<Order, bool> filter)
    {
        var m = await _masters.LoadAsync();
        var orders = (await _store.GetAllAsync<Order>()).Where(filter).ToList();
        var ids = orders.Select(o => o.Id).ToHashSet();
        var items = (await _store.GetAllAsync<OrderItem>()).Where(i => ids.Contains(i.OrderId)).ToLookup(i => i.OrderId);

        return orders
            .OrderByDescending(o => o.Date).ThenByDescending(o => o.Time).ThenByDescending(o => o.OrderNo)
            .Select(o => ToView(o, items[o.Id], m))
            .ToList();
    }

    public static OrderView ToView(Order o, IEnumerable<OrderItem> items, MasterSnapshot m)
    {
        m.Parties.TryGetValue(o.PartyId, out var party);
        return new OrderView
        {
            Id = o.Id,
            OrderNo = o.OrderNo,
            Date = o.Date,
            Time = o.Time,
            Status = o.Status,
            PartyName = party?.Name ?? "",
            PartyAddress = party?.Address ?? "",
            PartyContactPerson = party?.ContactPerson ?? "",
            PartyPhone = party?.Phone ?? "",
            SalesPersonName = m.UserName(o.UserId),
            BeatName = m.BeatName(o.BeatId),
            DealerName = m.DealerName(o.DealerId),
            TotalValue = o.TotalValue,
            CancelReason = o.CancelReason,
            BilledByName = m.UserName(o.BilledBy),
            BilledAt = o.BilledAt,
            CancelledByName = m.UserName(o.CancelledBy),
            CancelledAt = o.CancelledAt,
            Lat = o.Lat,
            Lng = o.Lng,
            LocationName = o.LocationName,
            Items = items.Select(i =>
            {
                m.Products.TryGetValue(i.ProductId, out var p);
                return new OrderItemView
                {
                    ProductCode = p?.Code ?? "",
                    ProductName = p?.Name ?? i.ProductId,
                    Qty = i.Qty,
                    UnitName = m.UnitName(p, i.Unit),
                    Rate = i.Rate,
                    Discount = i.Discount,
                    FreeQty = i.FreeQty,
                    LineValue = i.LineValue,
                    SchemeName = m.SchemeName(i.SchemeId)
                };
            }).ToList()
        };
    }

    public async Task<OrderView> MarkBilledAsync(string orderId, string byUserId)
    {
        await _store.WriteLockedAsync(async () =>
        {
            var order = await _store.FindAsync<Order>(orderId) ?? throw AppException.NotFound("Order not found.");
            if (order.Status == OrderStatus.Billed) throw AppException.Conflict($"Order {order.OrderNo} is already billed.");

            order.Status = OrderStatus.Billed;
            order.BilledBy = byUserId;
            order.BilledAt = AppClock.StampText(_clock.Now);
            await _store.UpdateAsync(order);
        });
        return await GetAsync(orderId);
    }

    public async Task<OrderView> CancelAsync(string orderId, string byUserId, string reason)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length < 3 || reason.Length > 200) throw AppException.BadRequest("Reason must be 3 to 200 characters.");

        await _store.WriteLockedAsync(async () =>
        {
            var order = await _store.FindAsync<Order>(orderId) ?? throw AppException.NotFound("Order not found.");
            if (order.Status == OrderStatus.Billed)
                throw AppException.Conflict($"Order {order.OrderNo} is already billed and cannot be cancelled.");

            order.Status = OrderStatus.Cancelled;
            order.CancelReason = reason;
            order.CancelledBy = byUserId;
            order.CancelledAt = AppClock.StampText(_clock.Now);
            await _store.UpdateAsync(order);
        });
        return await GetAsync(orderId);
    }

    public async Task<OrderView> GetAsync(string orderId)
    {
        var m = await _masters.LoadAsync();
        var order = await _store.FindAsync<Order>(orderId) ?? throw AppException.NotFound("Order not found.");
        var items = (await _store.GetAllAsync<OrderItem>()).Where(i => i.OrderId == orderId);
        return ToView(order, items, m);
    }
}

[ApiController]
[Route("api/billing")]
[Authorize(Roles = Roles.AdminOrBill)]
public sealed class BillingController : ControllerBase
{
    private readonly BillingService _billing;

    public BillingController(BillingService billing) => _billing = billing;

    [HttpGet("today")]
    public async Task<ActionResult<List<OrderView>>> Today() => Ok(await _billing.TodayAsync());

    [HttpGet("pending")]
    public async Task<ActionResult<List<OrderView>>> Pending() => Ok(await _billing.PendingAsync());

    [HttpPost("{id}/bill")]
    public async Task<ActionResult<OrderView>> Bill(string id) => Ok(await _billing.MarkBilledAsync(id, User.UserId()));

    [HttpPost("{id}/cancel")]
    public async Task<ActionResult<OrderView>> Cancel(string id, [FromBody] CancelOrderRequest req) =>
        Ok(await _billing.CancelAsync(id, User.UserId(), req.Reason));
}
