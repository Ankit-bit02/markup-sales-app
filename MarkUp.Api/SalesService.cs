using System.Globalization;
using System.Text;
using MarkUp.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MarkUp.Api;

public sealed class SalesService
{
    private const int MaxLinesPerOrder = 100;

    private readonly SheetStore _store;
    private readonly MasterData _masters;
    private readonly AppClock _clock;
    private readonly GeocodingService _geo;
    private readonly AppOptions _app;

    public SalesService(SheetStore store, MasterData masters, AppClock clock, GeocodingService geo, IOptions<AppOptions> app)
    {
        _store = store;
        _masters = masters;
        _clock = clock;
        _geo = geo;
        _app = app.Value;
    }

    /// <summary>Everything the Sales screen needs, also cached on the phone for offline use.</summary>
    public async Task<SalesBootstrap> BootstrapAsync(string userId)
    {
        var m = await _masters.LoadAsync();
        if (!m.Users.TryGetValue(userId, out var user)) throw AppException.NotFound("User not found.");

        var today = _clock.Today;
        var beats = m.Beats.Values.Where(b => b.IsActive && b.AssignedUserId == userId).OrderBy(b => b.Name).ToList();
        var beatIds = beats.Select(b => b.Id).ToHashSet();
        var parties = m.Parties.Values.Where(p => p.IsActive && beatIds.Contains(p.BeatId)).OrderBy(p => p.Name).ToList();
        var brands = (await _store.GetAllAsync<Brand>()).Where(b => b.IsActive).OrderBy(b => b.Name).ToList();
        var inactiveBrandIds = (await _store.GetAllAsync<Brand>()).Where(b => !b.IsActive).Select(b => b.Id).ToHashSet();
        var products = m.Products.Values
            .Where(p => p.IsActive && !inactiveBrandIds.Contains(p.BrandId))
            .OrderBy(p => p.Name).ToList();
        var productIds = products.Select(p => p.Id).ToHashSet();
        var schemes = m.Schemes.Values
            .Where(s => s.IsActive && s.EndDate >= today && productIds.Contains(s.ProductId))
            .OrderBy(s => s.Name).ToList();
        var partyTypes = m.PartyTypes.Values.Where(t => t.IsActive).OrderBy(t => t.Name).ToList();

        var attendance = (await _store.GetAllAsync<Attendance>())
            .FirstOrDefault(a => a.UserId == userId && a.Date == today);

        return new SalesBootstrap
        {
            UserId = user.Id,
            UserName = user.Name,
            DealerName = m.DealerName(user.DealerId),
            Today = today,
            AttendanceMarkedToday = attendance != null,
            AttendanceTime = attendance?.LoginTime ?? "",
            AttendanceLocation = attendance?.LocationName ?? "",
            Beats = beats,
            Parties = parties,
            Brands = brands,
            Products = products,
            Schemes = schemes,
            PartyTypes = partyTypes,
            LoadedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>Validates a device timestamp (records may be saved offline and synced later).</summary>
    private DateTime ResolveCapturedAt(DateTime capturedAt)
    {
        var now = _clock.Now;
        if (capturedAt == default) return now;
        if (capturedAt > now.AddMinutes(10))
            throw AppException.BadRequest("The phone's date/time is ahead of the real time. Fix the phone clock and try again.");
        if (capturedAt < now.AddDays(-Math.Max(1, _app.MaxOfflineDays)))
            throw AppException.BadRequest($"This record is older than {_app.MaxOfflineDays} days and cannot be synced.");
        return capturedAt;
    }

    private static void ValidateLocation(double lat, double lng)
    {
        if (double.IsNaN(lat) || double.IsNaN(lng) || lat < -90 || lat > 90 || lng < -180 || lng > 180 || (lat == 0 && lng == 0))
            throw AppException.BadRequest("A valid GPS location is required. Turn on location and try again.");
    }

    public async Task<AttendanceResult> MarkAttendanceAsync(string userId, AttendanceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Id)) throw AppException.BadRequest("Missing record id.");
        ValidateLocation(req.Lat, req.Lng);
        var at = ResolveCapturedAt(req.CapturedAt);
        var date = DateOnly.FromDateTime(at);

        // Look up the place name before taking the write lock (it can take a second).
        var location = await _geo.LocationNameAsync(req.Lat, req.Lng);

        return await _store.WriteLockedAsync(async () =>
        {
            var all = await _store.GetAllAsync<Attendance>();
            var existing = all.FirstOrDefault(a => a.Id == req.Id)
                           ?? all.FirstOrDefault(a => a.UserId == userId && a.Date == date);
            if (existing != null)
            {
                if (existing.UserId != userId) throw AppException.Conflict("Record id already used.");
                return new AttendanceResult
                {
                    Id = existing.Id, Date = existing.Date, LoginTime = existing.LoginTime,
                    LocationName = existing.LocationName, AlreadyMarked = true
                };
            }

            var row = new Attendance
            {
                Id = req.Id.Trim(),
                UserId = userId,
                Date = date,
                Day = date.DayOfWeek.ToString(),
                LoginTime = AppClock.TimeText(at),
                Lat = req.Lat,
                Lng = req.Lng,
                LocationName = location,
                SyncedAt = _clock.Now
            };
            await _store.AddAsync(row);
            return new AttendanceResult { Id = row.Id, Date = row.Date, LoginTime = row.LoginTime, LocationName = row.LocationName };
        });
    }

    public async Task<VisitResult> SaveVisitAsync(string userId, VisitRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Id)) throw AppException.BadRequest("Missing record id.");
        ValidateLocation(req.Lat, req.Lng);
        var at = ResolveCapturedAt(req.CapturedAt);
        var date = DateOnly.FromDateTime(at);
        var items = req.Items ?? new List<OrderLineRequest>();

        var m = await _masters.LoadAsync();
        if (!m.Users.TryGetValue(userId, out var user)) throw AppException.NotFound("User not found.");

        if (!m.Beats.TryGetValue(req.BeatId, out var beat) || beat.AssignedUserId != userId)
            throw AppException.BadRequest("This beat is not assigned to you.");
        if (!m.Parties.TryGetValue(req.PartyId, out var party) || party.BeatId != beat.Id)
            throw AppException.BadRequest("This party does not belong to the selected beat.");
        if (!party.IsActive) throw AppException.BadRequest($"Party \"{party.Name}\" is inactive.");

        var reason = (req.NoOrderReason ?? "").Trim();
        var productive = items.Count > 0;
        if (!productive && reason.Length < 3)
            throw AppException.BadRequest("Enter a reason (at least 3 characters) when there is no order.");
        if (items.Count > MaxLinesPerOrder)
            throw AppException.BadRequest($"An order can have at most {MaxLinesPerOrder} lines.");

        // Price every line on the server. Totals from the phone are never trusted.
        var priced = new List<(OrderLineRequest Line, Product Product, Scheme? Scheme, LineCalc Calc)>();
        foreach (var line in items)
        {
            if (!m.Products.TryGetValue(line.ProductId ?? "", out var product) || !product.IsActive)
                throw AppException.BadRequest("An ordered product is not available any more. Remove it and save again.");
            if (!UnitKinds.IsValid(line.Unit)) throw AppException.BadRequest("Invalid unit.");
            if (line.Unit == UnitKinds.Second && string.IsNullOrWhiteSpace(product.SecondUnit))
                throw AppException.BadRequest($"{product.Name} has no 2nd unit.");
            if (line.Qty <= 0 || line.Qty > 1_000_000) throw AppException.BadRequest($"Enter a valid quantity for {product.Name}.");

            Scheme? scheme = null;
            if (!string.IsNullOrWhiteSpace(line.SchemeId))
            {
                if (!m.Schemes.TryGetValue(line.SchemeId, out scheme) || scheme.ProductId != product.Id)
                    throw AppException.BadRequest($"The selected scheme is not valid for {product.Name}.");
            }

            var calc = Pricing.Calculate(product, line.Qty, line.Unit, scheme, date);
            if (scheme != null && !calc.SchemeApplied)
                throw AppException.BadRequest($"Scheme \"{scheme.Name}\" does not apply to {product.Name} (check dates and minimum quantity).");

            priced.Add((line, product, scheme, calc));
        }

        var location = await _geo.LocationNameAsync(req.Lat, req.Lng);

        return await _store.WriteLockedAsync(async () =>
        {
            var visits = await _store.GetAllAsync<Visit>();
            var orders = await _store.GetAllAsync<Order>();

            var existingVisit = visits.FirstOrDefault(v => v.Id == req.Id);
            if (existingVisit != null)
            {
                if (existingVisit.UserId != userId) throw AppException.Conflict("Record id already used.");
                var existingOrder = orders.FirstOrDefault(o => o.VisitId == existingVisit.Id);
                return BuildResult(existingVisit, existingOrder, party, user, beat, m, await ItemsFor(existingOrder), true);
            }

            Order? order = orders.FirstOrDefault(o => o.VisitId == req.Id); // left over from an interrupted earlier attempt
            var orderItems = new List<OrderItem>();

            if (productive)
            {
                if (order == null)
                {
                    order = new Order
                    {
                        Id = Ids.New(),
                        OrderNo = NextOrderNo(orders, date),
                        VisitId = req.Id.Trim(),
                        Date = date,
                        Time = AppClock.TimeText(at),
                        UserId = userId,
                        DealerId = beat.DealerId,
                        BeatId = beat.Id,
                        PartyId = party.Id,
                        TotalValue = priced.Sum(p => p.Calc.LineValue),
                        Status = OrderStatus.Pending,
                        Lat = req.Lat,
                        Lng = req.Lng,
                        LocationName = location,
                        SyncedAt = _clock.Now
                    };
                    await _store.AddAsync(order);
                }

                orderItems = await ItemsFor(order);
                if (orderItems.Count == 0)
                {
                    orderItems = priced.Select(p => new OrderItem
                    {
                        Id = Ids.New(),
                        OrderId = order.Id,
                        ProductId = p.Product.Id,
                        Qty = p.Line.Qty,
                        Unit = p.Line.Unit,
                        BaseQty = p.Calc.BaseQty,
                        Rate = p.Calc.Rate,
                        Gross = p.Calc.Gross,
                        SchemeId = p.Scheme?.Id ?? "",
                        Discount = p.Calc.Discount,
                        FreeQty = p.Calc.FreeQty,
                        LineValue = p.Calc.LineValue
                    }).ToList();
                    await _store.AddRangeAsync(orderItems);
                }
            }

            var visit = new Visit
            {
                Id = req.Id.Trim(),
                UserId = userId,
                Date = date,
                Day = date.DayOfWeek.ToString(),
                Time = AppClock.TimeText(at),
                BeatId = beat.Id,
                PartyId = party.Id,
                Lat = req.Lat,
                Lng = req.Lng,
                LocationName = location,
                IsProductive = productive,
                NoOrderReason = productive ? "" : reason,
                SyncedAt = _clock.Now
            };
            await _store.AddAsync(visit);

            return BuildResult(visit, productive ? order : null, party, user, beat, m, orderItems, false);
        });
    }

    private async Task<List<OrderItem>> ItemsFor(Order? order)
    {
        if (order == null) return new List<OrderItem>();
        var all = await _store.GetAllAsync<OrderItem>();
        return all.Where(i => i.OrderId == order.Id).ToList();
    }

    private static string NextOrderNo(List<Order> orders, DateOnly date)
    {
        var prefix = $"ORD-{date:yyyyMMdd}-";
        var max = 0;
        foreach (var o in orders)
        {
            if (o.OrderNo.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(o.OrderNo.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                && n > max)
            {
                max = n;
            }
        }
        return prefix + (max + 1).ToString("0000", CultureInfo.InvariantCulture);
    }

    private static VisitResult BuildResult(Visit visit, Order? order, Party party, UserRecord user, Beat beat,
        MasterSnapshot m, List<OrderItem> items, bool already)
    {
        var result = new VisitResult
        {
            VisitId = visit.Id,
            Productive = visit.IsProductive,
            PartyName = party.Name,
            LocationName = visit.LocationName,
            AlreadySynced = already
        };
        if (order == null) return result;

        result.OrderId = order.Id;
        result.OrderNo = order.OrderNo;
        result.TotalValue = order.TotalValue;

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"*New Order {order.OrderNo}*");
        sb.AppendLine($"Date: {order.Date:yyyy-MM-dd} {order.Time}");
        sb.AppendLine($"Party: {party.Name}{(string.IsNullOrWhiteSpace(party.Address) ? "" : ", " + party.Address)}");
        sb.AppendLine($"Beat: {beat.Name}");
        sb.AppendLine($"Salesperson: {user.Name}");
        sb.AppendLine("Items:");
        foreach (var it in items)
        {
            m.Products.TryGetValue(it.ProductId, out var p);
            var name = p?.Name ?? it.ProductId;
            var unit = m.UnitName(p, it.Unit);
            var free = it.FreeQty > 0 ? $" (+{it.FreeQty.ToString("0.###", inv)} free)" : "";
            sb.AppendLine($"- {name}: {it.Qty.ToString("0.###", inv)} {unit}{free} = NPR {it.LineValue.ToString("#,##0.00", inv)}");
        }
        sb.AppendLine($"*Total: NPR {order.TotalValue.ToString("#,##0.00", inv)}*");
        sb.AppendLine($"Location: {order.LocationName}");
        sb.Append(string.Create(inv, $"https://maps.google.com/?q={order.Lat:0.000000},{order.Lng:0.000000}"));
        result.WhatsAppText = sb.ToString();
        return result;
    }
}

[ApiController]
[Route("api/sales")]
[Authorize(Roles = Roles.Sales)]
public sealed class SalesController : ControllerBase
{
    private readonly SalesService _sales;

    public SalesController(SalesService sales) => _sales = sales;

    [HttpGet("bootstrap")]
    public async Task<ActionResult<SalesBootstrap>> Bootstrap() => Ok(await _sales.BootstrapAsync(User.UserId()));

    [HttpPost("attendance")]
    public async Task<ActionResult<AttendanceResult>> Attendance([FromBody] AttendanceRequest req) =>
        Ok(await _sales.MarkAttendanceAsync(User.UserId(), req));

    [HttpPost("visits")]
    public async Task<ActionResult<VisitResult>> Visit([FromBody] VisitRequest req) =>
        Ok(await _sales.SaveVisitAsync(User.UserId(), req));
}
