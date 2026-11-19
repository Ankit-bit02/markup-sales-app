using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MarkUp.Api;

/// <summary>
/// Turns GPS coordinates into a short place name using OpenStreetMap Nominatim.
/// Respects the public usage policy: max 1 request per second, identifying User-Agent, results cached.
/// Never throws: if the lookup fails, an empty string is returned and coordinates are used instead.
/// </summary>
public sealed class GeocodingService
{
    public const string HttpClientName = "nominatim";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GeocodingService> _logger;
    private readonly bool _enabled;
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastCallUtc = DateTime.MinValue;

    public GeocodingService(IHttpClientFactory httpFactory, IOptions<AppOptions> options, ILogger<GeocodingService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _enabled = options.Value.ReverseGeocoding;
    }

    public static string Coordinates(double lat, double lng) =>
        string.Create(CultureInfo.InvariantCulture, $"{lat:0.00000}, {lng:0.00000}");

    public async Task<string> LocationNameAsync(double lat, double lng)
    {
        if (!_enabled) return Coordinates(lat, lng);

        var key = string.Create(CultureInfo.InvariantCulture, $"{lat:0.0000},{lng:0.0000}");
        if (_cache.TryGetValue(key, out var cached)) return cached;

        await _throttle.WaitAsync();
        try
        {
            if (_cache.TryGetValue(key, out cached)) return cached;

            var wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - _lastCallUtc);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            _lastCallUtc = DateTime.UtcNow;

            var client = _httpFactory.CreateClient(HttpClientName);
            var url = string.Create(CultureInfo.InvariantCulture,
                $"reverse?format=jsonv2&zoom=16&addressdetails=1&lat={lat:0.000000}&lon={lng:0.000000}");
            using var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return Coordinates(lat, lng);

            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var name = ShortName(doc.RootElement);
            if (string.IsNullOrWhiteSpace(name)) name = Coordinates(lat, lng);
            if (name.Length > 150) name = name[..150];

            _cache[key] = name;
            return name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reverse geocoding failed for {Lat},{Lng}.", lat, lng);
            return Coordinates(lat, lng);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static string ShortName(JsonElement root)
    {
        if (root.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.Object)
        {
            var parts = new List<string>();
            foreach (var key in new[] { "road", "neighbourhood", "suburb", "village", "town", "city_district", "city", "county" })
            {
                if (addr.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && !parts.Contains(s)) parts.Add(s);
                }
                if (parts.Count == 3) break;
            }
            if (parts.Count > 0) return string.Join(", ", parts);
        }

        if (root.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String)
        {
            var full = dn.GetString() ?? "";
            return string.Join(", ", full.Split(',', StringSplitOptions.TrimEntries).Take(3));
        }
        return "";
    }
}
