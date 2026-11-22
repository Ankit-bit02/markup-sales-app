using System.Text.Json;
using MarkUp.Shared;

namespace MarkUp.Web;

public sealed class QueuedRecord
{
    public string Id { get; set; } = "";
    /// <summary>"attendance" or "visit".</summary>
    public string Kind { get; set; } = "";
    public string Json { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>Set when the server rejected the record. It then waits for the user to fix or remove it.</summary>
    public string Error { get; set; } = "";
}

/// <summary>
/// Every attendance/visit is first saved on the phone, then sent to the server.
/// If there is no network, records wait here and are sent automatically later, in order.
/// Each record has a fixed Id, so sending it twice never creates duplicates.
/// </summary>
public sealed class OfflineSync
{
    public const string KindAttendance = "attendance";
    public const string KindVisit = "visit";
    public const string KindParty = "party";
    private const int MaxResults = 30;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly Browser _browser;
    private readonly ApiClient _api;
    private readonly AuthSession _auth;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OfflineSync(Browser browser, ApiClient api, AuthSession auth)
    {
        _browser = browser;
        _api = api;
        _auth = auth;
    }

    public event Action? Changed;

    public bool IsSyncing { get; private set; }
    public string LastMessage { get; private set; } = "";

    private string QueueKey => $"mk_queue_{_auth.UserId}";
    private string ResultsKey => $"mk_results_{_auth.UserId}";
    private string BootKey => $"mk_boot_{_auth.UserId}";
    private string AttendanceKey => $"mk_att_{_auth.UserId}";

    // ------------------------------------------------------------------ Queue

    public async Task<List<QueuedRecord>> GetQueueAsync() =>
        await _browser.GetJsonAsync<List<QueuedRecord>>(QueueKey) ?? new List<QueuedRecord>();

    private Task SaveQueueAsync(List<QueuedRecord> queue) => _browser.SetJsonAsync(QueueKey, queue);

    public async Task EnqueueAsync(string kind, string id, object payload, string label)
    {
        await _gate.WaitAsync();
        try
        {
            var queue = await GetQueueAsync();
            if (queue.All(q => q.Id != id))
            {
                queue.Add(new QueuedRecord
                {
                    Id = id,
                    Kind = kind,
                    Json = JsonSerializer.Serialize(payload, payload.GetType(), JsonOpts),
                    Label = label,
                    CreatedAt = DateTime.Now
                });
                var saved = await _browser.SetJsonAsync(QueueKey, queue);
                if (!saved) throw new InvalidOperationException("Phone storage is full. Sync or clear old data before saving more.");
            }
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    public async Task RemoveAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var queue = await GetQueueAsync();
            queue.RemoveAll(q => q.Id == id);
            await SaveQueueAsync(queue);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    public async Task RetryAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var queue = await GetQueueAsync();
            foreach (var q in queue.Where(q => q.Id == id)) q.Error = "";
            await SaveQueueAsync(queue);
        }
        finally
        {
            _gate.Release();
        }
        await SyncAsync();
    }

    /// <summary>Sends waiting records in order. Stops at the first network problem and tries again later.</summary>
    public async Task SyncAsync()
    {
        if (!_auth.IsLoggedIn || _auth.Role != Roles.Sales) return;
        if (!await _gate.WaitAsync(0)) return; // already syncing

        IsSyncing = true;
        Changed?.Invoke();
        try
        {
            var queue = await GetQueueAsync();
            foreach (var record in queue.ToList())
            {
                if (!string.IsNullOrEmpty(record.Error)) continue;
                try
                {
                    if (record.Kind == KindAttendance)
                    {
                        var req = JsonSerializer.Deserialize<AttendanceRequest>(record.Json, JsonOpts)!;
                        var res = await _api.PostAsync<AttendanceResult>("api/sales/attendance", req);
                        await _browser.SetItemAsync(AttendanceKey, res.Date.ToString("yyyy-MM-dd"));
                    }
                    else if (record.Kind == KindParty)
                    {
                        // Queued before the visits that use it, so the shop always exists first.
                        var req = JsonSerializer.Deserialize<NewPartyRequest>(record.Json, JsonOpts)!;
                        await _api.PostAsync<Party>("api/sales/parties", req);
                    }
                    else if (record.Kind == KindVisit)
                    {
                        var req = JsonSerializer.Deserialize<VisitRequest>(record.Json, JsonOpts)!;
                        var res = await _api.PostAsync<VisitResult>("api/sales/visits", req);
                        await AddResultAsync(res);
                    }
                    queue.Remove(record);
                    await SaveQueueAsync(queue);
                    LastMessage = "";
                }
                catch (ApiException ex) when (ex.IsTransient)
                {
                    LastMessage = ex.IsNetworkError ? "Offline - records will be sent when you are back online." : ex.Message;
                    break;
                }
                catch (ApiException ex) when (ex.StatusCode == 401)
                {
                    LastMessage = ex.Message;
                    break;
                }
                catch (ApiException ex)
                {
                    record.Error = ex.Message;
                    await SaveQueueAsync(queue);
                }
            }
        }
        finally
        {
            IsSyncing = false;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    // ------------------------------------------------------------------ Synced results (for WhatsApp sharing)

    public async Task<List<VisitResult>> GetResultsAsync() =>
        await _browser.GetJsonAsync<List<VisitResult>>(ResultsKey) ?? new List<VisitResult>();

    private async Task AddResultAsync(VisitResult result)
    {
        var list = await GetResultsAsync();
        list.RemoveAll(r => r.VisitId == result.VisitId);
        list.Insert(0, result);
        if (list.Count > MaxResults) list.RemoveRange(MaxResults, list.Count - MaxResults);
        await _browser.SetJsonAsync(ResultsKey, list);
    }

    public async Task MarkSharedAsync(string visitId)
    {
        var list = await GetResultsAsync();
        list.RemoveAll(r => r.VisitId == visitId);
        await _browser.SetJsonAsync(ResultsKey, list);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ Offline master data and attendance flag

    public Task<SalesBootstrap?> GetCachedBootstrapAsync() => _browser.GetJsonAsync<SalesBootstrap>(BootKey);

    public Task SaveBootstrapAsync(SalesBootstrap boot) => _browser.SetJsonAsync(BootKey, boot);

    /// <summary>The date (yyyy-MM-dd) attendance was last captured on this phone.</summary>
    public async Task<string> GetLocalAttendanceDateAsync() => await _browser.GetItemAsync(AttendanceKey) ?? "";

    public async Task SetLocalAttendanceDateAsync(DateOnly date) =>
        await _browser.SetItemAsync(AttendanceKey, date.ToString("yyyy-MM-dd"));
}
