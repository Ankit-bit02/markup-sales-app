using System.Globalization;
using MarkUp.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarkUp.Api;

public sealed class AttendanceService
{
    private readonly SheetStore _store;
    private readonly MasterData _masters;
    private readonly AppClock _clock;

    public AttendanceService(SheetStore store, MasterData masters, AppClock clock)
    {
        _store = store;
        _masters = masters;
        _clock = clock;
    }

    /// <summary>Today's record for one user; Id is empty when they have not signed in yet.</summary>
    public async Task<AttendanceResult> TodayAsync(string userId)
    {
        var today = _clock.Today;
        var row = (await _store.GetAllAsync<Attendance>()).FirstOrDefault(a => a.UserId == userId && a.Date == today);
        return row == null
            ? new AttendanceResult { Date = today }
            : new AttendanceResult
            {
                Id = row.Id,
                Date = row.Date,
                LoginTime = row.LoginTime,
                LocationName = row.LocationName,
                NoGps = row.NoGps,
                AlreadyMarked = true
            };
    }

    /// <summary>
    /// Everyone who should sign in (active Sales and Bill users) for one day, with their sign-in time.
    /// People who have not signed in are listed without a time; nothing is marked late or absent.
    /// </summary>
    public async Task<DailyAttendance> DailyAsync(DateOnly date)
    {
        var m = await _masters.LoadAsync();
        var records = (await _store.GetAllAsync<Attendance>())
            .Where(a => a.Date == date)
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.LoginTime, StringComparer.Ordinal).First());

        // Active field staff, plus anyone who signed in that day even if since deactivated.
        var userIds = m.Users.Values
            .Where(u => u.IsActive && (u.Role == Roles.Sales || u.Role == Roles.Bill))
            .Select(u => u.Id)
            .Union(records.Keys)
            .ToList();

        var rows = new List<AttendanceRow>();
        foreach (var id in userIds)
        {
            m.Users.TryGetValue(id, out var u);
            records.TryGetValue(id, out var a);
            rows.Add(new AttendanceRow
            {
                UserId = id,
                Name = u?.Name ?? "(deleted user)",
                Role = u?.Role ?? "",
                SignedIn = a != null,
                LoginTime = a?.LoginTime ?? "",
                LocationName = a?.LocationName ?? "",
                Lat = a == null || a.NoGps ? null : a.Lat,
                Lng = a == null || a.NoGps ? null : a.Lng,
                NoGps = a?.NoGps ?? false
            });
        }

        rows = rows
            .OrderByDescending(r => r.SignedIn)
            .ThenBy(r => r.SignedIn ? r.LoginTime : "", StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DailyAttendance
        {
            Date = date,
            Day = date.DayOfWeek.ToString(),
            Total = rows.Count,
            SignedIn = rows.Count(r => r.SignedIn),
            Rows = rows
        };
    }
}

[ApiController]
[Route("api/attendance")]
[Authorize]
public sealed class AttendanceController : ControllerBase
{
    private readonly AttendanceService _attendance;
    private readonly SalesService _sales;
    private readonly AppClock _clock;

    public AttendanceController(AttendanceService attendance, SalesService sales, AppClock clock)
    {
        _attendance = attendance;
        _sales = sales;
        _clock = clock;
    }

    /// <summary>First sign-in of the day for Sales and Bill. Bill may send NoGps when location is refused.</summary>
    [HttpPost]
    [Authorize(Roles = Roles.Sales + "," + Roles.Bill)]
    public async Task<ActionResult<AttendanceResult>> Mark([FromBody] AttendanceRequest req) =>
        Ok(await _sales.MarkAttendanceAsync(User.UserId(), req, allowNoGps: User.Role() == Roles.Bill));

    /// <summary>Has the signed-in user marked attendance today?</summary>
    [HttpGet("me")]
    [Authorize(Roles = Roles.Sales + "," + Roles.Bill)]
    public async Task<ActionResult<AttendanceResult>> Me() => Ok(await _attendance.TodayAsync(User.UserId()));

    /// <summary>GET /api/attendance/daily?date=yyyy-MM-dd (Admin). Defaults to today.</summary>
    [HttpGet("daily")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<DailyAttendance>> Daily([FromQuery] string? date)
    {
        var day = _clock.Today;
        if (!string.IsNullOrWhiteSpace(date) &&
            !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            throw AppException.BadRequest("Date must be in yyyy-MM-dd format.");
        if (day > _clock.Today) throw AppException.BadRequest("Date cannot be in the future.");
        return Ok(await _attendance.DailyAsync(day));
    }
}