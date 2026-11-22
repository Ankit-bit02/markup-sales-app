using System.Globalization;
using MarkUp.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarkUp.Api;

public sealed class ReportQuery
{
    public DateOnly Date { get; set; }
    public string UserId { get; set; } = "";
    public string DealerId { get; set; } = "";
    public string BeatId { get; set; } = "";
    public string View { get; set; } = TargetViews.Daily;
    /// <summary>Date range for range reports (Attendance). Both default to Date.</summary>
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
}

public sealed class ReportService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly SheetStore _store;
    private readonly MasterData _masters;

    public ReportService(SheetStore store, MasterData masters)
    {
        _store = store;
        _masters = masters;
    }

    public static string Money(decimal v) => v.ToString("#,##0.00", Inv);
    public static string Qty(decimal v) => v.ToString("0.###", Inv);
    private static string Pct(decimal ach, decimal target) => target > 0 ? (ach / target * 100m).ToString("0.0", Inv) + "%" : "-";
    private static string DateText(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    public Task<ReportTable> BuildAsync(string kind, ReportQuery q) => kind switch
    {
        ReportKinds.PartyList => PartyListAsync(q),
        ReportKinds.Dsr => DsrAsync(q),
        ReportKinds.Products => ProductsAsync(),
        ReportKinds.TargetVsAch => TargetVsAchAsync(q),
        ReportKinds.ProductTarget => ProductTargetAsync(q),
        ReportKinds.Attendance => AttendanceAsync(q),
        _ => throw AppException.NotFound("Unknown report.")
    };

    // ------------------------------------------------------------------ Party Name List

    private async Task<ReportTable> PartyListAsync(ReportQuery q)
    {
        var m = await _masters.LoadAsync();
        var rows = m.Parties.Values
            .Where(p => p.IsActive)
            .Select(p => new { Party = p, Beat = m.Beats.GetValueOrDefault(p.BeatId) })
            .Where(x => x.Beat != null)
            .Where(x => string.IsNullOrEmpty(q.UserId) || x.Beat!.AssignedUserId == q.UserId)
            .Where(x => string.IsNullOrEmpty(q.DealerId) || x.Beat!.DealerId == q.DealerId)
            .Where(x => string.IsNullOrEmpty(q.BeatId) || x.Beat!.Id == q.BeatId)
            .OrderBy(x => m.UserName(x.Beat!.AssignedUserId))
            .ThenBy(x => m.DealerName(x.Beat!.DealerId))
            .ThenBy(x => x.Beat!.Name)
            .ThenBy(x => x.Party.Name)
            .ToList();

        var t = new ReportTable
        {
            Title = "Party Name List",
            Header =
            {
                new("User", string.IsNullOrEmpty(q.UserId) ? "All" : m.UserName(q.UserId)),
                new("Dealer", string.IsNullOrEmpty(q.DealerId) ? "All" : m.DealerName(q.DealerId)),
                new("Beat", string.IsNullOrEmpty(q.BeatId) ? "All" : m.BeatName(q.BeatId))
            },
            Columns = { "SN", "User Name", "Dealer Name", "Beat Map Name", "Party Name", "Contact Person", "Address", "Contact Number", "Party Type" }
        };

        var sn = 1;
        foreach (var x in rows)
        {
            t.Rows.Add(new List<string>
            {
                (sn++).ToString(Inv), m.UserName(x.Beat!.AssignedUserId), m.DealerName(x.Beat.DealerId), x.Beat.Name,
                x.Party.Name, x.Party.ContactPerson, x.Party.Address, x.Party.Phone, m.PartyTypeName(x.Party.PartyTypeId)
            });
        }
        t.Footer.Add(new("Total parties", rows.Count.ToString(Inv)));
        return t;
    }

    // ------------------------------------------------------------------ Daily DSR

    private async Task<ReportTable> DsrAsync(ReportQuery q)
    {
        var m = await _masters.LoadAsync();
        var allUsers = string.IsNullOrEmpty(q.UserId);

        var visits = (await _store.GetAllAsync<Visit>())
            .Where(v => v.Date == q.Date && (allUsers || v.UserId == q.UserId))
            .OrderBy(v => m.UserName(v.UserId)).ThenBy(v => v.Time)
            .ToList();
        var visitIds = visits.Select(v => v.Id).ToHashSet();
        var orders = (await _store.GetAllAsync<Order>())
            .Where(o => visitIds.Contains(o.VisitId))
            .GroupBy(o => o.VisitId)
            .ToDictionary(g => g.Key, g => g.First());
        var orderIds = orders.Values.Select(o => o.Id).ToHashSet();
        var items = (await _store.GetAllAsync<OrderItem>()).Where(i => orderIds.Contains(i.OrderId)).ToLookup(i => i.OrderId);

        string dealer, person;
        if (allUsers)
        {
            person = "All";
            dealer = "All";
        }
        else
        {
            person = m.UserName(q.UserId);
            dealer = m.Users.TryGetValue(q.UserId, out var u) ? m.DealerName(u.DealerId) : "";
        }
        var beatNames = string.Join(", ", visits.Select(v => m.BeatName(v.BeatId)).Where(n => n.Length > 0).Distinct());

        var t = new ReportTable
        {
            Title = "Daily Sales Report (DSR)",
            Header =
            {
                new("Report Date", DateText(q.Date)),
                new("Day", q.Date.DayOfWeek.ToString()),
                new("Sales Person Name", person),
                new("Dealer", dealer),
                new("Beat", beatNames.Length > 0 ? beatNames : "-")
            }
        };
        t.Columns.Add("SN");
        if (allUsers) t.Columns.Add("Sales Person");
        t.Columns.AddRange(new[] { "Party Name", "Address", "Cont Person", "Cont No", "Product Name", "Qty", "Value", "Scheme", "Productivity", "No-order Reason" });
        t.NumericColumns.Add(t.Columns.IndexOf("Value"));

        var sn = 1;
        decimal total = 0;
        foreach (var v in visits)
        {
            m.Parties.TryGetValue(v.PartyId, out var party);
            List<string> Start()
            {
                var r = new List<string> { sn.ToString(Inv) };
                if (allUsers) r.Add(m.UserName(v.UserId));
                r.AddRange(new[] { party?.Name ?? "", party?.Address ?? "", party?.ContactPerson ?? "", party?.Phone ?? "" });
                return r;
            }

            if (v.IsProductive && orders.TryGetValue(v.Id, out var order))
            {
                total += order.TotalValue;
                var lines = items[order.Id].ToList();
                if (lines.Count == 0)
                {
                    var r = Start();
                    r.AddRange(new[] { "", "", Money(order.TotalValue), "", "Yes", "" });
                    t.Rows.Add(r);
                }
                foreach (var it in lines)
                {
                    m.Products.TryGetValue(it.ProductId, out var p);
                    var qty = $"{Qty(it.Qty)} {m.UnitName(p, it.Unit)}" + (it.FreeQty > 0 ? $" (+{Qty(it.FreeQty)} free)" : "");
                    var r = Start();
                    r.AddRange(new[] { p?.Name ?? "", qty, Money(it.LineValue), m.SchemeName(it.SchemeId), "Yes", "" });
                    t.Rows.Add(r);
                }
            }
            else
            {
                var r = Start();
                r.AddRange(new[] { "", "", "", "", "No", v.NoOrderReason });
                t.Rows.Add(r);
            }
            sn++;
        }

        var productive = visits.Count(v => v.IsProductive);
        t.Footer.Add(new("Total visits", visits.Count.ToString(Inv)));
        t.Footer.Add(new("Productive visits", productive.ToString(Inv)));
        t.Footer.Add(new("Total order value (NPR)", Money(total)));
        return t;
    }

    // ------------------------------------------------------------------ Product report

    private async Task<ReportTable> ProductsAsync()
    {
        var m = await _masters.LoadAsync();
        var t = new ReportTable
        {
            Title = "Product Report",
            Columns = { "P. Code", "Group Name", "Product Name", "Unit", "2nd Unit", "MRP NPR", "MRP INR", "DLR Rate", "RT Rate" },
            NumericColumns = { 5, 6, 7, 8 }
        };
        foreach (var p in m.Products.Values.Where(p => p.IsActive).OrderBy(p => p.GroupName).ThenBy(p => p.Name))
        {
            var second = string.IsNullOrWhiteSpace(p.SecondUnit) ? "" : $"{p.SecondUnit} ({Qty(p.ConvFactor)} {p.Unit})";
            t.Rows.Add(new List<string>
            {
                p.Code, p.GroupName, p.Name, p.Unit, second,
                Money(p.MrpNpr), Money(p.MrpInr), Money(p.DealerRate), Money(p.RetailerRate)
            });
        }
        t.Footer.Add(new("Total products", t.Rows.Count.ToString(Inv)));
        return t;
    }

    // ------------------------------------------------------------------ Working days

    private sealed class WorkCalendar
    {
        private readonly Dictionary<int, HashSet<DayOfWeek>> _offs;
        private readonly HashSet<DateOnly> _holidays;

        public WorkCalendar(List<CalendarSetting> settings, List<Holiday> holidays)
        {
            _offs = settings.Where(s => s.IsActive)
                .GroupBy(s => s.Year)
                .ToDictionary(g => g.Key, g => Pricing.ParseWeeklyOffs(g.First().WeeklyOffDays));
            _holidays = holidays.Where(h => h.IsActive).Select(h => h.Date).ToHashSet();
        }

        /// <summary>Saturday is the default weekly off when a year has no calendar setting.</summary>
        private HashSet<DayOfWeek> OffsFor(int year) =>
            _offs.TryGetValue(year, out var s) ? s : new HashSet<DayOfWeek> { DayOfWeek.Saturday };

        public int Count(DateOnly start, DateOnly end)
        {
            var n = 0;
            for (var d = start; d <= end; d = d.AddDays(1))
            {
                if (OffsFor(d.Year).Contains(d.DayOfWeek) || _holidays.Contains(d)) continue;
                n++;
            }
            return n;
        }

        public bool IsWorkingDay(DateOnly d) => Count(d, d) == 1;
    }

    private async Task<WorkCalendar> CalendarAsync() =>
        new(await _store.GetAllAsync<CalendarSetting>(), await _store.GetAllAsync<Holiday>());

    private static (DateOnly First, DateOnly Last) MonthOf(DateOnly d)
    {
        var first = new DateOnly(d.Year, d.Month, 1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    // ------------------------------------------------------------------ Target vs Achievement (value)

    private async Task<ReportTable> TargetVsAchAsync(ReportQuery q)
    {
        var view = q.View switch
        {
            TargetViews.Mtd or TargetViews.Monthly or TargetViews.Overall => q.View,
            _ => TargetViews.Daily
        };

        var m = await _masters.LoadAsync();
        var cal = await CalendarAsync();
        var allUsers = string.IsNullOrEmpty(q.UserId);
        bool InScope(string userId) => allUsers || userId == q.UserId;

        var (first, last) = MonthOf(q.Date);
        var workDays = cal.Count(first, last);
        var elapsed = cal.Count(first, q.Date);

        var targets = (await _store.GetAllAsync<Target>()).Where(x => x.IsActive && InScope(x.UserId)).ToList();
        var monthTarget = targets.Where(x => x.Year == q.Date.Year && x.Month == q.Date.Month).Sum(x => x.TargetValue);
        var dailyTarget = workDays > 0 ? monthTarget / workDays : 0;

        DateOnly fromDate, toDate;
        decimal target;
        string label;
        switch (view)
        {
            case TargetViews.Mtd:
                (fromDate, toDate, target, label) = (first, q.Date, dailyTarget * elapsed, "MTD");
                break;
            case TargetViews.Monthly:
                (fromDate, toDate, target, label) = (first, last, monthTarget, "Month");
                break;
            case TargetViews.Overall:
                var upTo = q.Date.Year * 100 + q.Date.Month;
                (fromDate, toDate, target, label) = (DateOnly.MinValue, q.Date,
                    targets.Where(x => x.Year * 100 + x.Month <= upTo).Sum(x => x.TargetValue), "Overall");
                break;
            default:
                var isWorking = cal.IsWorkingDay(q.Date);
                (fromDate, toDate, target, label) = (q.Date, q.Date, isWorking ? dailyTarget : 0, "Day");
                break;
        }

        var orders = (await _store.GetAllAsync<Order>())
            .Where(o => InScope(o.UserId) && o.Date >= fromDate && o.Date <= toDate)
            .OrderBy(o => o.Date).ThenBy(o => o.Time).ThenBy(o => o.OrderNo)
            .ToList();
        var achieved = orders.Where(o => o.Status == OrderStatus.Billed).Sum(o => o.TotalValue);

        var t = new ReportTable
        {
            Title = $"Target vs Achievement - {label}",
            Header =
            {
                new("Sales Person Name", allUsers ? "All" : m.UserName(q.UserId)),
                new("Report Date", DateText(q.Date)),
                new($"{label} Target (NPR)", Money(target)),
                new($"{label} Achievement (NPR)", Money(achieved)),
                new("Achievement %", Pct(achieved, target))
            }
        };

        if (view == TargetViews.Overall)
        {
            t.Columns.AddRange(new[] { "Month", "Date", "Party Name", "Address", "Cont Person / Cont No", "Beat Map Name", "Sales Person Name", "Bill Value", "Remarks" });
        }
        else
        {
            t.Columns.AddRange(new[] { "SN", "Party Name", "Address", "Cont Person", "Cont No", "Beat Map Name", "Sales Person Name", "Bill Value", "Remarks" });
        }
        t.NumericColumns.Add(7);

        var sn = 1;
        foreach (var o in orders)
        {
            m.Parties.TryGetValue(o.PartyId, out var party);
            var billValue = o.Status == OrderStatus.Billed ? Money(o.TotalValue) : Money(0);
            var remarks = o.Status switch
            {
                OrderStatus.Cancelled => o.CancelReason,
                OrderStatus.Pending => "Not billed yet",
                _ => ""
            };

            if (view == TargetViews.Overall)
            {
                var contact = string.Join(" / ", new[] { party?.ContactPerson ?? "", party?.Phone ?? "" }.Where(s => s.Length > 0));
                t.Rows.Add(new List<string>
                {
                    o.Date.ToString("yyyy-MM", Inv), DateText(o.Date), party?.Name ?? "", party?.Address ?? "", contact,
                    m.BeatName(o.BeatId), m.UserName(o.UserId), billValue, remarks
                });
            }
            else
            {
                t.Rows.Add(new List<string>
                {
                    (sn++).ToString(Inv), party?.Name ?? "", party?.Address ?? "", party?.ContactPerson ?? "", party?.Phone ?? "",
                    m.BeatName(o.BeatId), m.UserName(o.UserId), billValue, remarks
                });
            }
        }

        t.Footer.Add(new("Orders listed", orders.Count.ToString(Inv)));
        t.Footer.Add(new("Billed orders", orders.Count(o => o.Status == OrderStatus.Billed).ToString(Inv)));
        if (view != TargetViews.Overall)
        {
            t.Footer.Add(new("Working days in month", workDays.ToString(Inv)));
            t.Footer.Add(new("Working days to date", elapsed.ToString(Inv)));
        }
        return t;
    }

    // ------------------------------------------------------------------ Product target vs achievement (quantity)

    private async Task<ReportTable> ProductTargetAsync(ReportQuery q)
    {
        var view = q.View switch
        {
            TargetViews.Mtd => TargetViews.Mtd,
            TargetViews.Monthly or TargetViews.Overall => TargetViews.Monthly,
            _ => TargetViews.Daily
        };

        var m = await _masters.LoadAsync();
        var cal = await CalendarAsync();
        var allUsers = string.IsNullOrEmpty(q.UserId);
        bool InScope(string userId) => allUsers || userId == q.UserId;

        var (first, last) = MonthOf(q.Date);
        var workDays = cal.Count(first, last);
        var elapsed = cal.Count(first, q.Date);
        var isWorking = cal.IsWorkingDay(q.Date);

        var (fromDate, toDate, label) = view switch
        {
            TargetViews.Mtd => (first, q.Date, "MTD"),
            TargetViews.Monthly => (first, last, "Month"),
            _ => (q.Date, q.Date, "Day")
        };

        decimal Scale(decimal monthQty) => view switch
        {
            TargetViews.Monthly => monthQty,
            TargetViews.Mtd => workDays > 0 ? monthQty / workDays * elapsed : 0,
            _ => workDays > 0 && isWorking ? monthQty / workDays : 0
        };

        var targets = (await _store.GetAllAsync<ProductTarget>())
            .Where(x => x.IsActive && InScope(x.UserId) && x.Year == q.Date.Year && x.Month == q.Date.Month)
            .ToList();

        var billedOrders = (await _store.GetAllAsync<Order>())
            .Where(o => o.Status == OrderStatus.Billed && InScope(o.UserId) && o.Date >= fromDate && o.Date <= toDate)
            .ToDictionary(o => o.Id, o => o.UserId);
        var sold = (await _store.GetAllAsync<OrderItem>())
            .Where(i => billedOrders.ContainsKey(i.OrderId))
            .GroupBy(i => (UserId: billedOrders[i.OrderId], i.ProductId))
            .ToDictionary(g => g.Key, g => g.Sum(i => i.BaseQty));

        var keys = targets.Select(x => (UserId: x.UserId, ProductId: x.ProductId))
            .Concat(sold.Keys)
            .Distinct()
            .OrderBy(k => m.UserName(k.UserId))
            .ThenBy(k => m.Products.TryGetValue(k.ProductId, out var p) ? p.Name : k.ProductId)
            .ToList();

        var t = new ReportTable
        {
            Title = $"Product Target vs Achievement - {label}",
            Header =
            {
                new("Sales Person Name", allUsers ? "All" : m.UserName(q.UserId)),
                new("Report Date", DateText(q.Date)),
                new("Quantities", "In base units (e.g. Pcs)")
            },
            Columns = { "SN", "Sales Person Name", "Product Code", "Product Name", "Unit", $"{label} Target Qty", "Billed Qty", "Achievement %" },
            NumericColumns = { 5, 6 }
        };

        var sn = 1;
        foreach (var k in keys)
        {
            m.Products.TryGetValue(k.ProductId, out var p);
            var monthQty = targets.Where(x => x.UserId == k.UserId && x.ProductId == k.ProductId).Sum(x => x.TargetQty);
            var targetQty = Math.Round(Scale(monthQty), 2);
            var billed = sold.TryGetValue(k, out var s) ? s : 0;
            t.Rows.Add(new List<string>
            {
                (sn++).ToString(Inv), m.UserName(k.UserId), p?.Code ?? "", p?.Name ?? k.ProductId, p?.Unit ?? "",
                Qty(targetQty), Qty(billed), Pct(billed, targetQty)
            });
        }

        t.Footer.Add(new("Working days in month", workDays.ToString(Inv)));
        t.Footer.Add(new("Working days to date", elapsed.ToString(Inv)));
        return t;
    }
    // ------------------------------------------------------------------ Attendance

    private async Task<ReportTable> AttendanceAsync(ReportQuery q)
    {
        var m = await _masters.LoadAsync();
        var rows = (await _store.GetAllAsync<Attendance>())
            .Where(a => a.Date >= q.From && a.Date <= q.To)
            .Where(a => string.IsNullOrEmpty(q.UserId) || a.UserId == q.UserId)
            .OrderBy(a => a.Date)
            .ThenBy(a => a.LoginTime, StringComparer.Ordinal)
            .ThenBy(a => m.UserName(a.UserId))
            .ToList();

        var t = new ReportTable
        {
            Title = "Attendance Report",
            Header =
            {
                new("From", DateText(q.From)),
                new("To", DateText(q.To)),
                new("User", string.IsNullOrEmpty(q.UserId) ? "All" : m.UserName(q.UserId))
            },
            Columns = { "SN", "Date", "Day", "User Name", "Role", "Sign-in Time", "Location", "GPS" }
        };

        var sn = 1;
        foreach (var a in rows)
        {
            m.Users.TryGetValue(a.UserId, out var u);
            t.Rows.Add(new List<string>
            {
                (sn++).ToString(Inv), DateText(a.Date), a.Date.DayOfWeek.ToString(), u?.Name ?? a.UserId, u?.Role ?? "",
                a.LoginTime, a.NoGps ? "No GPS" : a.LocationName,
                a.NoGps ? "No GPS" : string.Create(Inv, $"{a.Lat:0.000000}, {a.Lng:0.000000}")
            });
        }

        t.Footer.Add(new("Sign-ins", rows.Count.ToString(Inv)));
        t.Footer.Add(new("People", rows.Select(a => a.UserId).Distinct().Count().ToString(Inv)));
        t.Footer.Add(new("Days", (q.To.DayNumber - q.From.DayNumber + 1).ToString(Inv)));
        return t;
    }
}

[ApiController]
[Route("api/reports")]
[Authorize]
public sealed class ReportsController : ControllerBase
{
    private readonly ReportService _reports;
    private readonly ReportExporter _exporter;
    private readonly AppClock _clock;

    public ReportsController(ReportService reports, ReportExporter exporter, AppClock clock)
    {
        _reports = reports;
        _exporter = exporter;
        _clock = clock;
    }

    /// <summary>
    /// GET /api/reports/{kind}?date=yyyy-MM-dd&amp;from=yyyy-MM-dd&amp;to=yyyy-MM-dd&amp;userId=&amp;dealerId=&amp;beatId=&amp;view=daily|mtd|monthly|overall&amp;format=json|xlsx|pdf
    /// from/to are used by the Attendance report (both default to date).
    /// Sales users always get their own data only; Bill users get only their own attendance.
    /// </summary>
    [HttpGet("{kind}")]
    public async Task<IActionResult> Get(string kind, [FromQuery] string? date, [FromQuery] string? userId,
        [FromQuery] string? dealerId, [FromQuery] string? beatId, [FromQuery] string? view, [FromQuery] string? format,
        [FromQuery] string? from, [FromQuery] string? to)
    {
        var day = ParseDate(date, _clock.Today);
        var fromDay = ParseDate(from, day);
        var toDay = ParseDate(to, day);
        if (fromDay > toDay) throw AppException.BadRequest("From date must be on or before To date.");
        if (toDay.DayNumber - fromDay.DayNumber > 366) throw AppException.BadRequest("Pick a range of one year or less.");

        // Sales see only their own data in every report; Bill see only their own attendance.
        var role = User.Role();
        var ownOnly = role == Roles.Sales || (role == Roles.Bill && kind == ReportKinds.Attendance);

        var q = new ReportQuery
        {
            Date = day,
            From = fromDay,
            To = toDay,
            UserId = ownOnly ? User.UserId() : (userId ?? "").Trim(),
            DealerId = (dealerId ?? "").Trim(),
            BeatId = (beatId ?? "").Trim(),
            View = (view ?? TargetViews.Daily).Trim().ToLowerInvariant()
        };

        var table = await _reports.BuildAsync(kind, q);
        var fileBase = kind == ReportKinds.Attendance
            ? $"{kind}-{fromDay:yyyy-MM-dd}_to_{toDay:yyyy-MM-dd}"
            : $"{kind}-{day:yyyy-MM-dd}";

        return (format ?? "json").ToLowerInvariant() switch
        {
            "xlsx" => File(_exporter.ToExcel(table),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileBase + ".xlsx"),
            "pdf" => File(_exporter.ToPdf(table), "application/pdf", fileBase + ".pdf"),
            _ => Ok(table)
        };
    }

    private static DateOnly ParseDate(string? text, DateOnly fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            throw AppException.BadRequest("Dates must be in yyyy-MM-dd format.");
        return d;
    }
}