using MarkUp.Shared;
using Microsoft.JSInterop;

namespace MarkUp.Web;

/// <summary>
/// Beat tracking on the salesperson's phone.
/// Start beat .. End beat: GPS points are recorded while the app is open (see markup.trackStart in app.js),
/// kept on the phone and uploaded in small batches. Works offline; points wait until the connection returns.
/// </summary>
public sealed class TrackingClient : IDisposable
{
    /// <summary>A point at least every minute, or when the person moved 50 m.</summary>
    private const int IntervalMs = 60_000;
    private const int DistanceM = 50;

    private const int MaxQueue = 3000;
    private const int BatchSize = 200;

    private sealed class TrackState
    {
        public string UserId { get; set; } = "";
        public string BeatId { get; set; } = "";
        public DateTime StartedAt { get; set; }
    }

    private readonly IJSRuntime _js;
    private readonly Browser _browser;
    private readonly ApiClient _api;
    private readonly AuthSession _auth;
    private DotNetObjectReference<TrackingClient>? _selfRef;
    private Timer? _timer;
    private List<TrackPointRequest> _queue = new();
    private TrackState? _state;
    private bool _uploading;
    private bool _loadedFor;
    private string _loadedUser = "";
    private double? _lastLat;
    private double? _lastLng;

    public TrackingClient(IJSRuntime js, Browser browser, ApiClient api, AuthSession auth)
    {
        _js = js;
        _browser = browser;
        _api = api;
        _auth = auth;
    }

    public event Action? Changed;

    public bool IsTracking => _state != null;
    public DateTime? StartedAt => _state?.StartedAt;
    public string BeatId => _state?.BeatId ?? "";
    public DateTime? LastPointAt { get; private set; }
    public int Waiting => _queue.Count;
    public string? LastError { get; private set; }

    /// <summary>False when the browser cannot keep the screen awake; the person must keep it on.</summary>
    public bool WakeLockOk { get; private set; } = true;

    private string StateKey => $"mk_track_state_{_auth.UserId}";
    private string QueueKey => $"mk_track_queue_{_auth.UserId}";
    private string ConsentKey => $"mk_track_consent_{_auth.UserId}";

    public async Task<bool> HasConsentAsync() => await _browser.GetItemAsync(ConsentKey) == "yes";

    public async Task SaveConsentAsync() => await _browser.SetItemAsync(ConsentKey, "yes");

    /// <summary>Loads saved state after sign-in or a page reload and continues tracking the same day.</summary>
    public async Task ResumeAsync()
    {
        if (!_auth.IsLoggedIn || _auth.Role != Roles.Sales) return;
        if (_loadedFor && _loadedUser == _auth.UserId) return;
        _loadedFor = true;
        _loadedUser = _auth.UserId;

        _queue = await _browser.GetJsonAsync<List<TrackPointRequest>>(QueueKey) ?? new List<TrackPointRequest>();
        var saved = await _browser.GetJsonAsync<TrackState>(StateKey);
        EnsureTimer();

        if (saved != null && saved.UserId == _auth.UserId)
        {
            if (DateOnly.FromDateTime(saved.StartedAt) == Fmt.Today())
            {
                _state = saved;
                await StartWatchAsync();
            }
            else
            {
                // Started on an earlier day and never ended: drop it (the office sees a gap after the last point).
                await _browser.RemoveItemAsync(StateKey);
            }
        }
        Changed?.Invoke();
        _ = FlushAsync();
    }

    public async Task<string?> StartAsync(string beatId)
    {
        if (_state != null) return null;
        var geo = await _browser.GetLocationAsync();
        if (!geo.Ok) return geo.Error ?? "Could not get your location.";

        _state = new TrackState { UserId = _auth.UserId, BeatId = beatId, StartedAt = Fmt.LocalNow() };
        await _browser.SetJsonAsync(StateKey, _state);
        await AddPointAsync(TrackKinds.Start, geo.Lat, geo.Lng, geo.Accuracy);
        await StartWatchAsync();
        EnsureTimer();
        Changed?.Invoke();
        _ = FlushAsync();
        return null;
    }

    public async Task StopAsync()
    {
        if (_state == null) return;
        await _js.InvokeVoidAsync("markup.trackStop");

        double lat, lng, acc = 0;
        if (_lastLat is { } la && _lastLng is { } ln)
        {
            lat = la;
            lng = ln;
        }
        else
        {
            var geo = await _browser.GetLocationAsync();
            lat = geo.Ok ? geo.Lat : 0;
            lng = geo.Ok ? geo.Lng : 0;
            acc = geo.Accuracy;
        }
        if (lat != 0 || lng != 0) await AddPointAsync(TrackKinds.End, lat, lng, acc);

        _state = null;
        LastError = null;
        await _browser.RemoveItemAsync(StateKey);
        Changed?.Invoke();
        await FlushAsync();
    }

    /// <summary>Called on log out: ends tracking and forgets this user's state in memory.</summary>
    public async Task SignOutAsync()
    {
        await StopAsync();
        _loadedFor = false;
        _loadedUser = "";
        _queue = new List<TrackPointRequest>();
    }

    [JSInvokable]
    public async Task OnTrackPoint(double lat, double lng, double accuracy)
    {
        if (_state == null) return;
        if (!_auth.IsLoggedIn)
        {
            await _js.InvokeVoidAsync("markup.trackStop"); // session ended; resumes after the next sign-in
            _loadedFor = false;
            return;
        }
        LastError = null;

        // A new day started while the app was open: end yesterday's beat.
        if (DateOnly.FromDateTime(_state.StartedAt) != Fmt.Today())
        {
            await StopAsync();
            return;
        }
        await AddPointAsync(TrackKinds.Point, lat, lng, accuracy);
        Changed?.Invoke();
    }

    [JSInvokable]
    public void OnTrackError(string message)
    {
        LastError = message;
        Changed?.Invoke();
    }

    /// <summary>Sends waiting points. Safe to call often; only one upload runs at a time.</summary>
    public async Task FlushAsync()
    {
        if (_uploading || _queue.Count == 0 || !_auth.IsLoggedIn) return;
        _uploading = true;
        try
        {
            while (_queue.Count > 0)
            {
                var batch = _queue.Take(BatchSize).ToList();
                TrackUploadResult result;
                try
                {
                    result = await _api.PostAsync<TrackUploadResult>("api/tracking/points", new TrackUploadRequest { Points = batch });
                }
                catch (ApiException ex) when (ex.IsTransient)
                {
                    return; // offline or server busy: try again later
                }
                catch (ApiException ex) when (ex.StatusCode == 400)
                {
                    // The server refused the whole batch; drop it so the queue never gets stuck.
                    result = new TrackUploadResult { DoneIds = batch.Select(p => p.Id).ToList() };
                }

                var done = result.DoneIds.ToHashSet();
                _queue.RemoveAll(p => done.Contains(p.Id));
                await SaveQueueAsync();
                if (done.Count == 0) return;
            }
        }
        catch (ApiException)
        {
            // signed out or no access: keep the points
        }
        finally
        {
            _uploading = false;
            Changed?.Invoke();
        }
    }

    private async Task StartWatchAsync()
    {
        _selfRef ??= DotNetObjectReference.Create(this);
        var res = await _js.InvokeAsync<StartResult>("markup.trackStart", _selfRef, IntervalMs, DistanceM);
        WakeLockOk = res.WakeLock;
        if (!res.Ok) LastError = res.Error;
    }

    private async Task AddPointAsync(string kind, double lat, double lng, double accuracy)
    {
        var now = Fmt.LocalNow();
        _queue.Add(new TrackPointRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            CapturedAt = now,
            Lat = lat,
            Lng = lng,
            Accuracy = Math.Round(accuracy),
            BeatId = _state?.BeatId ?? ""
        });
        if (_queue.Count > MaxQueue) _queue.RemoveRange(0, _queue.Count - MaxQueue);
        _lastLat = lat;
        _lastLng = lng;
        LastPointAt = now;
        await SaveQueueAsync();
    }

    private async Task SaveQueueAsync() => await _browser.SetJsonAsync(QueueKey, _queue);

    private void EnsureTimer()
    {
        // Upload when 3 or more points are waiting, or the oldest waited 3 minutes.
        _timer ??= new Timer(_ =>
        {
            if (_queue.Count == 0) return;
            var oldest = _queue[0].CapturedAt;
            if (_queue.Count >= 3 || Fmt.LocalNow() - oldest >= TimeSpan.FromMinutes(3)) _ = FlushAsync();
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _selfRef?.Dispose();
    }

    private sealed class StartResult
    {
        public bool Ok { get; set; }
        public bool WakeLock { get; set; }
        public string? Error { get; set; }
    }
}