using System.Collections.Concurrent;
using System.Globalization;
using MarkUp.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MarkUp.Api;

/// <summary>
/// Location tracking during a beat.
/// The phone records GPS points while the app is open (Start beat .. End beat) and uploads them in batches.
/// Points are stored in one Google Sheets tab per day ("Track_yyyy-MM-dd") so each day stays small;
/// tabs older than the retention period are deleted by <see cref="TrackingCleanupService"/>.
/// The latest point of each salesperson is also kept in memory for the live map.
/// </summary>
public sealed class TrackingService
{
    public const string TabPrefix = "Track_";

    private static readonly string[] Header = { "UserId", "Kind", "Time", "Lat", "Lng", "Accuracy", "BeatId" };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Points less accurate than this are kept but not drawn on the route.</summary>
    private const double RouteAccuracyLimit = 100;

    /// <summary>Points less accurate than this are not stored at all.</summary>
    private const double StoreAccuracyLimit = 1000;

    /// <summary>A gap longer than this between two points is shown as "not tracked".</summary>
    public const int GapMinutes = 5;

    private sealed record LivePoint(string Kind, DateTime At, double Lat, double Lng, double Accuracy, string BeatId);

    private readonly SheetStore _store;
    private readonly MasterData _masters;
    private readonly AppClock _clock;
    private readonly AppOptions _app;
    private readonly ConcurrentDictionary<string, LivePoint> _live = new();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private DateOnly _seededFor;

    public TrackingService(SheetStore store, MasterData masters, AppClock clock, IOptions<AppOptions> app)
    {
        _store = store;
        _masters = masters;
        _clock = clock;
        _app = app.Value;
    }

    public static string TabFor(DateOnly date) => TabPrefix + date.ToString("yyyy-MM-dd", Inv);

    // ------------------------------------------------------------------ Upload (salesperson)

    public async Task<TrackUploadResult> UploadAsync(string userId, TrackUploadRequest req)
    {
        var result = new TrackUploadResult();
        var now = _clock.Now;
        var oldest = now.AddDays(-Math.Max(1, _app.MaxOfflineDays));
        var byDay = new Dictionary<DateOnly, List<IList<object>>>();
        LivePoint? latest = null;

        foreach (var p in req.Points ?? new List<TrackPointRequest>())
        {
            if (string.IsNullOrWhiteSpace(p.Id)) continue;
            result.DoneIds.Add(p.Id);

            var valid = TrackKinds.IsValid(p.Kind)
                        && !double.IsNaN(p.Lat) && !double.IsNaN(p.Lng)
                        && p.Lat is >= -90 and <= 90 && p.Lng is >= -180 and <= 180
                        && !(p.Lat == 0 && p.Lng == 0)
                        && p.Accuracy >= 0 && p.Accuracy <= StoreAccuracyLimit
                        && p.CapturedAt != default && p.CapturedAt <= now.AddMinutes(10) && p.CapturedAt >= oldest;
            if (!valid)
            {
                result.Dropped++;
                continue;
            }

            var at = new DateTime(p.CapturedAt.Year, p.CapturedAt.Month, p.CapturedAt.Day,
                p.CapturedAt.Hour, p.CapturedAt.Minute, p.CapturedAt.Second, DateTimeKind.Unspecified);
            var day = DateOnly.FromDateTime(at);
            if (!byDay.TryGetValue(day, out var rows)) byDay[day] = rows = new List<IList<object>>();
            rows.Add(new List<object>
            {
                userId,
                p.Kind,
                at.ToString("yyyy-MM-dd HH:mm:ss", Inv),
                Math.Round(p.Lat, 6).ToString("R", Inv),
                Math.Round(p.Lng, 6).ToString("R", Inv),
                Math.Round(p.Accuracy).ToString(Inv),
                (p.BeatId ?? "").Trim()
            });
            result.Saved++;

            if (latest == null || at >= latest.At)
                latest = new LivePoint(p.Kind, at, p.Lat, p.Lng, p.Accuracy, (p.BeatId ?? "").Trim());
        }

        foreach (var (day, rows) in byDay.OrderBy(d => d.Key))
            await _store.AppendRawAsync(TabFor(day), Header, rows);

        if (latest != null && DateOnly.FromDateTime(latest.At) == _clock.Today)
        {
            _live.AddOrUpdate(userId, latest, (_, old) => latest.At >= old.At ? latest : old);
        }
        return result;
    }

    // ------------------------------------------------------------------ Live map (admin)

    public async Task<List<LiveLocation>> LiveAsync()
    {
        await SeedTodayAsync();
        var today = _clock.Today;
        var now = _clock.Now;
        var m = await _masters.LoadAsync();

        var visits = (await _store.GetAllAsync<Visit>()).Where(v => v.Date == today).ToList();
        var attendance = (await _store.GetAllAsync<Attendance>()).Where(a => a.Date == today).ToList();

        var list = new List<LiveLocation>();
        foreach (var u in m.Users.Values.Where(u => u.IsActive && u.Role == Roles.Sales).OrderBy(u => u.Name))
        {
            var beats = m.Beats.Values.Where(b => b.IsActive && b.AssignedUserId == u.Id).Select(b => b.Name).OrderBy(n => n);
            var item = new LiveLocation
            {
                UserId = u.Id,
                Name = u.Name,
                Phone = u.Phone,
                BeatNames = string.Join(", ", beats),
                AttendanceToday = attendance.Any(a => a.UserId == u.Id),
                VisitsToday = visits.Count(v => v.UserId == u.Id),
                OrdersToday = visits.Count(v => v.UserId == u.Id && v.IsProductive)
            };

            if (_live.TryGetValue(u.Id, out var p) && DateOnly.FromDateTime(p.At) == today)
            {
                item.HasLocation = true;
                item.Lat = p.Lat;
                item.Lng = p.Lng;
                item.Accuracy = Math.Round(p.Accuracy);
                item.LastSeen = p.At.ToString("HH:mm", Inv);
                item.MinutesAgo = Math.Max(0, (int)(now - p.At).TotalMinutes);
                item.IsTracking = p.Kind != TrackKinds.End && item.MinutesAgo <= 10;
            }
            list.Add(item);
        }
        return list;
    }

    /// <summary>After a restart the in-memory positions are empty: fill them once from today's tab.</summary>
    private async Task SeedTodayAsync()
    {
        var today = _clock.Today;
        if (_seededFor == today) return;
        await _seedLock.WaitAsync();
        try
        {
            if (_seededFor == today) return;
            foreach (var row in await _store.ReadRawAsync(TabFor(today), Header.Length))
            {
                if (!TryParse(row, out var userId, out var point)) continue;
                _live.AddOrUpdate(userId, point, (_, old) => point.At >= old.At ? point : old);
            }
            foreach (var key in _live.Where(kv => DateOnly.FromDateTime(kv.Value.At) != today).Select(kv => kv.Key).ToList())
                _live.TryRemove(key, out _);
            _seededFor = today;
        }
        finally
        {
            _seedLock.Release();
        }
    }

    // ------------------------------------------------------------------ Route of one day (admin)

    public async Task<RouteView> RouteAsync(string userId, DateOnly date)
    {
        var m = await _masters.LoadAsync();
        if (!m.Users.TryGetValue(userId, out var user)) throw AppException.NotFound("Salesperson not found.");

        var points = new List<LivePoint>();
        var seen = new HashSet<string>();
        foreach (var row in await _store.ReadRawAsync(TabFor(date), Header.Length))
        {
            if (!TryParse(row, out var rowUser, out var p) || rowUser != userId) continue;
            if (!seen.Add($"{p.Kind}|{p.At:O}")) continue; // a batch that was sent twice
            points.Add(p);
        }
        points.Sort((a, b) => a.At.CompareTo(b.At));

        var routePoints = points.Where(p => p.Kind != TrackKinds.Point || p.Accuracy <= RouteAccuracyLimit).ToList();

        var visits = (await _store.GetAllAsync<Visit>())
            .Where(v => v.UserId == userId && v.Date == date)
            .OrderBy(v => v.Time).ToList();
        var orders = (await _store.GetAllAsync<Order>())
            .Where(o => o.UserId == userId && o.Date == date)
            .ToDictionary(o => o.VisitId, o => o);
        var attendance = (await _store.GetAllAsync<Attendance>()).FirstOrDefault(a => a.UserId == userId && a.Date == date);

        var beats = m.Beats.Values.Where(b => b.IsActive && b.AssignedUserId == userId).ToList();
        var beatIds = beats.Select(b => b.Id).ToHashSet();
        var visitedPartyIds = visits.Select(v => v.PartyId).ToHashSet();

        var view = new RouteView
        {
            UserId = user.Id,
            UserName = user.Name,
            Date = date,
            BeatNames = string.Join(", ", beats.Select(b => b.Name).OrderBy(n => n)),
            AttendanceTime = attendance?.LoginTime ?? "",
            AttendanceLat = attendance?.Lat,
            AttendanceLng = attendance?.Lng,
            Points = routePoints.Select(p => new RoutePoint
            {
                Time = p.At.ToString("HH:mm:ss", Inv),
                Kind = p.Kind,
                Lat = p.Lat,
                Lng = p.Lng,
                Accuracy = Math.Round(p.Accuracy)
            }).ToList(),
            Stops = visits.Select(v =>
            {
                orders.TryGetValue(v.Id, out var order);
                return new RouteStop
                {
                    Time = v.Time,
                    Lat = v.Lat,
                    Lng = v.Lng,
                    PartyName = m.Parties.TryGetValue(v.PartyId, out var party) ? party.Name : "",
                    IsProductive = v.IsProductive,
                    OrderNo = order?.OrderNo ?? "",
                    OrderValue = order?.TotalValue ?? 0,
                    NoOrderReason = v.NoOrderReason
                };
            }).ToList(),
            Parties = m.Parties.Values
                .Where(p => p.IsActive && (beatIds.Contains(p.BeatId) || visitedPartyIds.Contains(p.Id)))
                .OrderBy(p => p.Name)
                .Select(p => new RouteParty
                {
                    Id = p.Id,
                    Name = p.Name,
                    Address = p.Address,
                    BeatName = m.BeatName(p.BeatId),
                    Lat = p.Lat,
                    Lng = p.Lng,
                    Visited = visitedPartyIds.Contains(p.Id)
                }).ToList(),
            FirstTime = routePoints.FirstOrDefault()?.At.ToString("HH:mm", Inv) ?? "",
            LastTime = routePoints.LastOrDefault()?.At.ToString("HH:mm", Inv) ?? ""
        };

        // Distance and tracked time: skip gaps and impossible jumps (GPS glitches).
        double km = 0, minutes = 0;
        for (var i = 1; i < routePoints.Count; i++)
        {
            var a = routePoints[i - 1];
            var b = routePoints[i];
            if (a.Kind == TrackKinds.End) continue;
            var mins = (b.At - a.At).TotalMinutes;
            if (mins <= 0 || mins > GapMinutes) continue;
            var d = DistanceKm(a.Lat, a.Lng, b.Lat, b.Lng);
            if (d / (mins / 60.0) > 150) continue;
            km += d;
            minutes += mins;
        }
        view.DistanceKm = Math.Round((decimal)km, 1);
        view.TrackedMinutes = (int)Math.Round(minutes);
        return view;
    }

    // ------------------------------------------------------------------ Helpers

    private static bool TryParse(string[] row, out string userId, out LivePoint point)
    {
        userId = row[0];
        point = null!;
        if (string.IsNullOrEmpty(userId)) return false;
        if (!DateTime.TryParseExact(row[2], "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out var at)) return false;
        if (!double.TryParse(row[3], NumberStyles.Float, Inv, out var lat)) return false;
        if (!double.TryParse(row[4], NumberStyles.Float, Inv, out var lng)) return false;
        double.TryParse(row[5], NumberStyles.Float, Inv, out var acc);
        point = new LivePoint(row[1], at, lat, lng, acc, row[6]);
        return true;
    }

    private static double DistanceKm(double lat1, double lng1, double lat2, double lng2)
    {
        const double r = 6371.0;
        double Rad(double d) => d * Math.PI / 180.0;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}

/// <summary>Deletes tracking tabs older than App:TrackingRetentionDays, twice a day.</summary>
public sealed class TrackingCleanupService : BackgroundService
{
    private readonly SheetStore _store;
    private readonly AppClock _clock;
    private readonly AppOptions _app;
    private readonly ILogger<TrackingCleanupService> _logger;

    public TrackingCleanupService(SheetStore store, AppClock clock, IOptions<AppOptions> app, ILogger<TrackingCleanupService> logger)
    {
        _store = store;
        _clock = clock;
        _app = app.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cutoff = _clock.Today.AddDays(-Math.Max(7, _app.TrackingRetentionDays));
                var old = (await _store.ListTabsAsync(TrackingService.TabPrefix))
                    .Where(t => DateOnly.TryParseExact(t[TrackingService.TabPrefix.Length..], "yyyy-MM-dd",
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < cutoff)
                    .ToList();
                if (old.Count > 0)
                {
                    await _store.DeleteTabsAsync(old);
                    _logger.LogInformation("Deleted {Count} old tracking tab(s).", old.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tracking cleanup failed; will retry later.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

[ApiController]
[Route("api/tracking")]
[Authorize]
public sealed class TrackingController : ControllerBase
{
    private readonly TrackingService _tracking;
    private readonly AppClock _clock;

    public TrackingController(TrackingService tracking, AppClock clock)
    {
        _tracking = tracking;
        _clock = clock;
    }

    /// <summary>Salesperson's phone sends a batch of GPS points.</summary>
    [HttpPost("points")]
    [Authorize(Roles = Roles.Sales)]
    public async Task<ActionResult<TrackUploadResult>> Upload([FromBody] TrackUploadRequest req) =>
        Ok(await _tracking.UploadAsync(User.UserId(), req));

    /// <summary>Last known position of every salesperson today.</summary>
    [HttpGet("live")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<List<LiveLocation>>> Live() => Ok(await _tracking.LiveAsync());

    /// <summary>Route, visits and beat shops of one salesperson on one day.</summary>
    [HttpGet("route")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<RouteView>> Route([FromQuery] string userId, [FromQuery] DateOnly? date)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw AppException.BadRequest("Select a salesperson.");
        var day = date ?? _clock.Today;
        if (day > _clock.Today) throw AppException.BadRequest("The date cannot be in the future.");
        return Ok(await _tracking.RouteAsync(userId, day));
    }
}