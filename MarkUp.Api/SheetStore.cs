using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using MarkUp.Shared;
using Microsoft.Extensions.Options;

namespace MarkUp.Api;

/// <summary>Maps each stored type to its Google Sheets tab.</summary>
public static class TableRegistry
{
    private static readonly Dictionary<Type, string> Tabs = new()
    {
        [typeof(UserRecord)] = "Users",
        [typeof(PartyType)] = "PartyTypes",
        [typeof(Holiday)] = "Holidays",
        [typeof(CalendarSetting)] = "CalendarSettings",
        [typeof(Dealer)] = "Dealers",
        [typeof(Beat)] = "Beats",
        [typeof(Party)] = "Parties",
        [typeof(Brand)] = "Brands",
        [typeof(Product)] = "Products",
        [typeof(Scheme)] = "Schemes",
        [typeof(Target)] = "Targets",
        [typeof(ProductTarget)] = "ProductTargets",
        [typeof(Attendance)] = "Attendance",
        [typeof(Visit)] = "Visits",
        [typeof(Order)] = "Orders",
        [typeof(OrderItem)] = "OrderItems",
        [typeof(UserProfileRecord)] = "Profiles",
        [typeof(AppSettings)] = "AppSettings",
    };

    public static IReadOnlyDictionary<Type, string> All => Tabs;

    public static string TabName(Type t) =>
        Tabs.TryGetValue(t, out var name) ? name : throw new InvalidOperationException($"{t.Name} is not a stored type.");

    /// <summary>Column names = public read/write properties in declaration order.</summary>
    public static List<string> Columns(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .OrderBy(p => p.MetadataToken)
            .Select(p => p.Name)
            .ToList();
}

/// <summary>Converts between objects and sheet rows (all values stored as plain text).</summary>
internal static class RowMapper<T> where T : class, new()
{
    private static readonly Dictionary<string, PropertyInfo> Props =
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

    public static T FromRow(IReadOnlyList<string> header, string[] values)
    {
        var item = new T();
        for (var i = 0; i < header.Count; i++)
        {
            if (!Props.TryGetValue(header[i], out var prop)) continue;
            var raw = i < values.Length ? values[i] : "";
            prop.SetValue(item, ValueText.Parse(raw, prop.PropertyType));
        }
        return item;
    }

    public static string[] ToRow(IReadOnlyList<string> header, T item)
    {
        var row = new string[header.Count];
        for (var i = 0; i < header.Count; i++)
        {
            row[i] = Props.TryGetValue(header[i], out var prop)
                ? ValueText.Format(prop.GetValue(item))
                : "";
        }
        return row;
    }
}

internal static class ValueText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "TRUE" : "FALSE",
        decimal d => d.ToString(Inv),
        double d => d.ToString("R", Inv),
        float f => f.ToString("R", Inv),
        int i => i.ToString(Inv),
        long l => l.ToString(Inv),
        DateOnly d => d.ToString("yyyy-MM-dd", Inv),
        DateTime dt => dt == default ? "" : dt.ToString("yyyy-MM-dd HH:mm:ss", Inv),
        _ => Convert.ToString(value, Inv) ?? ""
    };

    public static object? Parse(string? raw, Type type)
    {
        raw = raw?.Trim() ?? "";
        var t = Nullable.GetUnderlyingType(type) ?? type;

        if (t == typeof(string)) return raw;
        if (raw.Length == 0) return type == typeof(string) ? "" : (Nullable.GetUnderlyingType(type) != null ? null : DefaultOf(t));

        if (t == typeof(bool))
            return raw.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || raw == "1" || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
        if (t == typeof(int))
            return int.TryParse(raw, NumberStyles.Integer, Inv, out var i) ? i
                : (decimal.TryParse(raw, NumberStyles.Number, Inv, out var di) ? (int)di : 0);
        if (t == typeof(long))
            return long.TryParse(raw, NumberStyles.Integer, Inv, out var l) ? l : 0L;
        if (t == typeof(decimal))
            return decimal.TryParse(raw, NumberStyles.Number | NumberStyles.AllowExponent, Inv, out var d) ? d : 0m;
        if (t == typeof(double))
            return double.TryParse(raw, NumberStyles.Float, Inv, out var db) ? db : 0d;
        if (t == typeof(DateOnly))
        {
            if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var dOnly)) return dOnly;
            if (DateTime.TryParse(raw, Inv, DateTimeStyles.None, out var dt1)) return DateOnly.FromDateTime(dt1);
            if (double.TryParse(raw, NumberStyles.Float, Inv, out var serial)) return DateOnly.FromDateTime(DateTime.FromOADate(serial));
            return default(DateOnly);
        }
        if (t == typeof(DateTime))
        {
            if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out var dt)) return dt;
            if (DateTime.TryParse(raw, Inv, DateTimeStyles.None, out var dt2)) return dt2;
            if (double.TryParse(raw, NumberStyles.Float, Inv, out var serial2)) return DateTime.FromOADate(serial2);
            return default(DateTime);
        }
        return DefaultOf(t);
    }

    private static object? DefaultOf(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;
}

/// <summary>
/// Google Sheets as the data store.
/// - One tab per type, header row = property names.
/// - Values are written RAW (never interpreted as formulas).
/// - Tabs are cached in memory; this API is the only writer, so writes update the cache directly.
/// - All writes (and cache reloads) are serialised by one lock, so read-check-write sequences are atomic.
///   Run a single API instance.
/// </summary>
public sealed class SheetStore
{
    private sealed record CachedRow(int RowNumber, string[] Values);

    private sealed class Table
    {
        public required List<string> Header { get; init; }
        public required List<CachedRow> Rows { get; init; }
        public required DateTime LoadedAtUtc { get; init; }
    }

    private static readonly AsyncLocal<bool> InWriteLock = new();
    private static readonly Regex RangeRow = new(@"![A-Z]+(\d+)", RegexOptions.Compiled);

    private readonly SheetsService _service;
    private readonly string _spreadsheetId;
    private readonly TimeSpan _cacheTtl;
    private readonly bool _protectTabs;
    private readonly ILogger<SheetStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Table> _tables = new();

    public SheetStore(IOptions<GoogleSheetsOptions> options, ILogger<SheetStore> logger)
    {
        var o = options.Value;
        _logger = logger;
        _spreadsheetId = o.SpreadsheetId;
        _cacheTtl = TimeSpan.FromMinutes(Math.Max(1, o.CacheMinutes));
        _protectTabs = o.ProtectTabs;

        var json = LoadCredentialJson(o);
#pragma warning disable CS0618 // FromJson is marked obsolete in newer Google.Apis.Auth; the key comes from our own config.
        var credential = GoogleCredential.FromJson(json).CreateScoped(SheetsService.Scope.Spreadsheets);
#pragma warning restore CS0618

        _service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "MarkUp Sales API"
        });
    }

    private static string LoadCredentialJson(GoogleSheetsOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.CredentialsJson)) return o.CredentialsJson;
        if (!string.IsNullOrWhiteSpace(o.CredentialsBase64))
            return Encoding.UTF8.GetString(Convert.FromBase64String(o.CredentialsBase64.Trim()));
        if (!string.IsNullOrWhiteSpace(o.CredentialsPath))
        {
            if (!File.Exists(o.CredentialsPath))
                throw new InvalidOperationException($"Google credentials file not found: {o.CredentialsPath}");
            return File.ReadAllText(o.CredentialsPath);
        }
        throw new InvalidOperationException(
            "Google service account credentials are not configured. Set GoogleSheets:CredentialsPath, CredentialsJson or CredentialsBase64.");
    }

    // ------------------------------------------------------------------ Schema

    /// <summary>Creates missing tabs and header columns. Safe to run on every start.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        // Few round trips on purpose: this runs on every start (and Render's free plan restarts often).
        // 1) spreadsheet info, 2) all header rows in one batch read, 3) one batch write for missing
        // columns (only if needed), 4) one batch format/protect update (only if needed).
        var spreadsheet = await _service.Spreadsheets.Get(_spreadsheetId).ExecuteAsync(ct);
        var existing = spreadsheet.Sheets?.ToDictionary(s => s.Properties.Title, s => s, StringComparer.Ordinal)
                       ?? new Dictionary<string, Sheet>();

        var addRequests = TableRegistry.All.Values
            .Where(tab => !existing.ContainsKey(tab))
            .Select(tab => new Request { AddSheet = new AddSheetRequest { Properties = new SheetProperties { Title = tab } } })
            .ToList();

        if (addRequests.Count > 0)
        {
            await _service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = addRequests }, _spreadsheetId)
                .ExecuteAsync(ct);
            _logger.LogInformation("Created {Count} sheet tab(s).", addRequests.Count);
            spreadsheet = await _service.Spreadsheets.Get(_spreadsheetId).ExecuteAsync(ct);
            existing = spreadsheet.Sheets.ToDictionary(s => s.Properties.Title, s => s, StringComparer.Ordinal);
        }

        // All header rows in a single request.
        var tabs = TableRegistry.All.ToList();
        var batchGet = _service.Spreadsheets.Values.BatchGet(_spreadsheetId);
        batchGet.Ranges = tabs.Select(t => $"'{t.Value}'!1:1").ToList();
        var headers = await batchGet.ExecuteAsync(ct);

        var headerWrites = new List<ValueRange>();
        var formatRequests = new List<Request>();
        for (var i = 0; i < tabs.Count; i++)
        {
            var (type, tab) = tabs[i];
            var wanted = TableRegistry.Columns(type);
            var values = headers.ValueRanges != null && i < headers.ValueRanges.Count ? headers.ValueRanges[i].Values : null;
            var current = values?.FirstOrDefault()?.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").ToList()
                          ?? new List<string>();

            var missing = wanted.Where(c => !current.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
            {
                var startCol = current.Count + 1;
                headerWrites.Add(new ValueRange
                {
                    Range = $"'{tab}'!{ColumnLetter(startCol)}1",
                    Values = new List<IList<object>> { missing.Cast<object>().ToList() }
                });
                _logger.LogInformation("Tab {Tab}: adding columns {Columns}.", tab, string.Join(", ", missing));
            }

            var sheet = existing[tab];
            var sheetId = sheet.Properties.SheetId ?? 0;

            // A tab needs at least as many grid columns as header names (new tabs start with 26).
            var neededColumns = current.Count + missing.Count;
            var gridColumns = sheet.Properties.GridProperties?.ColumnCount ?? 26;
            if (neededColumns > gridColumns)
            {
                formatRequests.Insert(0, new Request
                {
                    AppendDimension = new AppendDimensionRequest { SheetId = sheetId, Dimension = "COLUMNS", Length = neededColumns - gridColumns }
                });
            }

            if ((sheet.Properties.GridProperties?.FrozenRowCount ?? 0) == 0)
            {
                formatRequests.Add(new Request
                {
                    UpdateSheetProperties = new UpdateSheetPropertiesRequest
                    {
                        Properties = new SheetProperties { SheetId = sheetId, GridProperties = new GridProperties { FrozenRowCount = 1 } },
                        Fields = "gridProperties.frozenRowCount"
                    }
                });
            }

            if (_protectTabs && (sheet.ProtectedRanges == null || sheet.ProtectedRanges.Count == 0))
            {
                formatRequests.Add(new Request
                {
                    AddProtectedRange = new AddProtectedRangeRequest
                    {
                        ProtectedRange = new ProtectedRange
                        {
                            Range = new GridRange { SheetId = sheetId },
                            Description = "Managed by the MARK_UP app. Edit data in the app, not here.",
                            WarningOnly = true
                        }
                    }
                });
            }
        }

        // Grid size first (so new header cells fit), then the header names.
        if (formatRequests.Count > 0)
        {
            await _service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = formatRequests }, _spreadsheetId)
                .ExecuteAsync(ct);
        }

        if (headerWrites.Count > 0)
        {
            var body = new BatchUpdateValuesRequest { ValueInputOption = "RAW", Data = headerWrites };
            await _service.Spreadsheets.Values.BatchUpdate(body, _spreadsheetId).ExecuteAsync(ct);
        }

        _tables.Clear();
    }

    // ------------------------------------------------------------------ Reads

    /// <summary>Returns fresh copies of all rows of a tab (callers may modify them freely).</summary>
    public async Task<List<T>> GetAllAsync<T>() where T : class, new()
    {
        var table = await GetTableAsync(TableRegistry.TabName(typeof(T)));
        var list = new List<T>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            list.Add(RowMapper<T>.FromRow(table.Header, row.Values));
        }
        return list;
    }

    public async Task<T?> FindAsync<T>(string id) where T : class, IEntity, new()
    {
        if (string.IsNullOrEmpty(id)) return null;
        var all = await GetAllAsync<T>();
        return all.FirstOrDefault(x => x.Id == id);
    }

    private async Task<Table> GetTableAsync(string tab)
    {
        if (_tables.TryGetValue(tab, out var cached) && DateTime.UtcNow - cached.LoadedAtUtc < _cacheTtl)
            return cached;

        // Reloads run under the write lock so a reload can never overwrite a newer write.
        return await WriteLockedAsync(async () =>
        {
            if (_tables.TryGetValue(tab, out var again) && DateTime.UtcNow - again.LoadedAtUtc < _cacheTtl)
                return again;
            var loaded = await LoadTableAsync(tab);
            _tables[tab] = loaded;
            return loaded;
        });
    }

    private async Task<Table> LoadTableAsync(string tab)
    {
        var req = _service.Spreadsheets.Values.Get(_spreadsheetId, $"'{tab}'!A1:ZZ");
        req.ValueRenderOption = SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.UNFORMATTEDVALUE;
        var resp = await req.ExecuteAsync();
        var values = resp.Values ?? new List<IList<object>>();

        var header = values.Count > 0
            ? values[0].Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? "").ToList()
            : new List<string>();

        var idIndex = header.FindIndex(h => h.Equals("Id", StringComparison.OrdinalIgnoreCase));
        var rows = new List<CachedRow>();
        for (var i = 1; i < values.Count; i++)
        {
            var cells = values[i];
            var arr = new string[header.Count];
            for (var c = 0; c < header.Count; c++)
            {
                arr[c] = c < cells.Count ? Convert.ToString(cells[c], CultureInfo.InvariantCulture) ?? "" : "";
            }
            if (idIndex >= 0 && string.IsNullOrWhiteSpace(arr[idIndex])) continue; // blank row
            rows.Add(new CachedRow(i + 1, arr));
        }

        return new Table { Header = header, Rows = rows, LoadedAtUtc = DateTime.UtcNow };
    }

    // ------------------------------------------------------------------ Writes

    /// <summary>
    /// Runs work under the single write lock. Every Add/Update must happen inside this call,
    /// together with the reads that decide what to write.
    /// </summary>
    public async Task<TResult> WriteLockedAsync<TResult>(Func<Task<TResult>> work)
    {
        if (InWriteLock.Value) return await work();

        await _writeLock.WaitAsync();
        try
        {
            InWriteLock.Value = true;
            return await work();
        }
        finally
        {
            InWriteLock.Value = false;
            _writeLock.Release();
        }
    }

    public Task WriteLockedAsync(Func<Task> work) =>
        WriteLockedAsync(async () =>
        {
            await work();
            return true;
        });

    public Task AddAsync<T>(T item) where T : class, IEntity, new() => AddRangeAsync(new[] { item });

    public async Task AddRangeAsync<T>(IReadOnlyList<T> items) where T : class, IEntity, new()
    {
        EnsureInLock();
        if (items.Count == 0) return;

        var tab = TableRegistry.TabName(typeof(T));
        var table = await GetTableAsync(tab);
        var rows = items.Select(it => RowMapper<T>.ToRow(table.Header, it)).ToList();

        var body = new ValueRange { Values = rows.Select(r => (IList<object>)r.Cast<object>().ToList()).ToList() };
        var req = _service.Spreadsheets.Values.Append(body, _spreadsheetId, $"'{tab}'!A1");
        req.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.RAW;
        req.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
        var resp = await req.ExecuteAsync();

        var firstRow = ParseFirstRow(resp.Updates?.UpdatedRange);
        if (firstRow <= 0)
        {
            // Could not read the row numbers back; force a reload on next access.
            _tables.TryRemove(tab, out _);
            return;
        }

        var newRows = new List<CachedRow>(table.Rows);
        for (var i = 0; i < rows.Count; i++) newRows.Add(new CachedRow(firstRow + i, rows[i]));
        _tables[tab] = new Table { Header = table.Header, Rows = newRows, LoadedAtUtc = table.LoadedAtUtc };
    }

    public async Task UpdateAsync<T>(T item) where T : class, IEntity, new()
    {
        EnsureInLock();
        var tab = TableRegistry.TabName(typeof(T));
        var table = await GetTableAsync(tab);
        var idIndex = table.Header.FindIndex(h => h.Equals("Id", StringComparison.OrdinalIgnoreCase));
        if (idIndex < 0) throw new InvalidOperationException($"Tab {tab} has no Id column.");

        var pos = table.Rows.FindIndex(r => r.Values[idIndex] == item.Id);
        if (pos < 0) throw AppException.NotFound("Record not found.");

        var existing = table.Rows[pos];
        var values = RowMapper<T>.ToRow(table.Header, item);
        var range = $"'{tab}'!A{existing.RowNumber}:{ColumnLetter(table.Header.Count)}{existing.RowNumber}";
        var body = new ValueRange { Values = new List<IList<object>> { values.Cast<object>().ToList() } };
        var req = _service.Spreadsheets.Values.Update(body, _spreadsheetId, range);
        req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
        await req.ExecuteAsync();

        var newRows = new List<CachedRow>(table.Rows);
        newRows[pos] = new CachedRow(existing.RowNumber, values);
        _tables[tab] = new Table { Header = table.Header, Rows = newRows, LoadedAtUtc = table.LoadedAtUtc };
    }




    /// <summary>Updates many existing rows of one tab in a single Google Sheets call.</summary>
    public async Task UpdateRangeAsync<T>(IReadOnlyList<T> items) where T : class, IEntity, new()
    {
        EnsureInLock();
        if (items.Count == 0) return;

        var tab = TableRegistry.TabName(typeof(T));
        var table = await GetTableAsync(tab);
        var idIndex = table.Header.FindIndex(h => h.Equals("Id", StringComparison.OrdinalIgnoreCase));
        if (idIndex < 0) throw new InvalidOperationException($"Tab {tab} has no Id column.");

        var newRows = new List<CachedRow>(table.Rows);
        var data = new List<ValueRange>();
        var lastColumn = ColumnLetter(table.Header.Count);
        foreach (var item in items)
        {
            var pos = newRows.FindIndex(r => r.Values[idIndex] == item.Id);
            if (pos < 0) throw AppException.NotFound("Record not found.");

            var rowNumber = newRows[pos].RowNumber;
            var values = RowMapper<T>.ToRow(table.Header, item);
            data.Add(new ValueRange
            {
                Range = $"'{tab}'!A{rowNumber}:{lastColumn}{rowNumber}",
                Values = new List<IList<object>> { values.Cast<object>().ToList() }
            });
            newRows[pos] = new CachedRow(rowNumber, values);
        }

        var body = new BatchUpdateValuesRequest { ValueInputOption = "RAW", Data = data };
        await _service.Spreadsheets.Values.BatchUpdate(body, _spreadsheetId).ExecuteAsync();

        _tables[tab] = new Table { Header = table.Header, Rows = newRows, LoadedAtUtc = table.LoadedAtUtc };
    }

    // ------------------------------------------------------------------ Raw tabs (location tracking: one tab per day)
    // These tabs are not cached and not part of TableRegistry. Rows are plain text values.

    private readonly ConcurrentDictionary<string, bool> _rawTabs = new();
    private readonly SemaphoreSlim _rawTabLock = new(1, 1);

    /// <summary>Appends rows to a tab, creating it (small, with a header row) the first time.</summary>
    public async Task AppendRawAsync(string tab, IReadOnlyList<string> header, IReadOnlyList<IList<object>> rows)
    {
        if (rows.Count == 0) return;
        await EnsureRawTabAsync(tab, header);
        var body = new ValueRange { Values = rows.ToList() };
        var req = _service.Spreadsheets.Values.Append(body, _spreadsheetId, $"'{tab}'!A1");
        req.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.RAW;
        req.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
        await req.ExecuteAsync();
    }

    /// <summary>All data rows of a tab (without the header). Empty when the tab does not exist.</summary>
    public async Task<List<string[]>> ReadRawAsync(string tab, int columns)
    {
        if (!await RawTabExistsAsync(tab)) return new List<string[]>();
        var resp = await _service.Spreadsheets.Values.Get(_spreadsheetId, $"'{tab}'!A2:{ColumnLetter(columns)}").ExecuteAsync();
        var list = new List<string[]>();
        foreach (var r in resp.Values ?? new List<IList<object>>())
        {
            var row = new string[columns];
            for (var i = 0; i < columns; i++)
                row[i] = i < r.Count ? Convert.ToString(r[i], CultureInfo.InvariantCulture) ?? "" : "";
            list.Add(row);
        }
        return list;
    }

    /// <summary>Titles of all tabs that start with the prefix.</summary>
    public async Task<List<string>> ListTabsAsync(string prefix)
    {
        var ss = await _service.Spreadsheets.Get(_spreadsheetId).ExecuteAsync();
        return (ss.Sheets ?? new List<Sheet>())
            .Select(s => s.Properties.Title)
            .Where(t => t.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
    }

    public async Task DeleteTabsAsync(IReadOnlyCollection<string> titles)
    {
        if (titles.Count == 0) return;
        var ss = await _service.Spreadsheets.Get(_spreadsheetId).ExecuteAsync();
        var requests = (ss.Sheets ?? new List<Sheet>())
            .Where(s => titles.Contains(s.Properties.Title))
            .Select(s => new Request { DeleteSheet = new DeleteSheetRequest { SheetId = s.Properties.SheetId } })
            .ToList();
        if (requests.Count == 0) return;
        await _service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = requests }, _spreadsheetId).ExecuteAsync();
        foreach (var t in titles) _rawTabs.TryRemove(t, out _);
    }

    private async Task<bool> RawTabExistsAsync(string tab)
    {
        if (_rawTabs.ContainsKey(tab)) return true;
        var ss = await _service.Spreadsheets.Get(_spreadsheetId).ExecuteAsync();
        var exists = ss.Sheets?.Any(s => s.Properties.Title == tab) ?? false;
        if (exists) _rawTabs[tab] = true;
        return exists;
    }

    private async Task EnsureRawTabAsync(string tab, IReadOnlyList<string> header)
    {
        if (_rawTabs.ContainsKey(tab)) return;
        await _rawTabLock.WaitAsync();
        try
        {
            if (await RawTabExistsAsync(tab)) return;

            // Keep the grid small: every cell of every tab counts towards Google's 10 million cell limit.
            var add = new Request
            {
                AddSheet = new AddSheetRequest
                {
                    Properties = new SheetProperties
                    {
                        Title = tab,
                        GridProperties = new GridProperties { RowCount = 2, ColumnCount = header.Count, FrozenRowCount = 1 }
                    }
                }
            };
            await _service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = new List<Request> { add } }, _spreadsheetId)
                .ExecuteAsync();

            var body = new ValueRange { Values = new List<IList<object>> { header.Cast<object>().ToList() } };
            var upd = _service.Spreadsheets.Values.Update(body, _spreadsheetId, $"'{tab}'!A1");
            upd.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
            await upd.ExecuteAsync();

            _rawTabs[tab] = true;
            _logger.LogInformation("Created tracking tab {Tab}.", tab);
        }
        finally
        {
            _rawTabLock.Release();
        }
    }
    private static void EnsureInLock()
    {
        if (!InWriteLock.Value)
            throw new InvalidOperationException("Sheet writes must run inside SheetStore.WriteLockedAsync.");
    }

    private static int ParseFirstRow(string? updatedRange)
    {
        if (string.IsNullOrEmpty(updatedRange)) return -1;
        var m = RangeRow.Match(updatedRange);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : -1;
    }

    private static string ColumnLetter(int column)
    {
        var sb = new StringBuilder();
        while (column > 0)
        {
            var rem = (column - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            column = (column - 1) / 26;
        }
        return sb.ToString();
    }
}
