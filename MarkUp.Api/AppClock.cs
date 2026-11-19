using Microsoft.Extensions.Options;

namespace MarkUp.Api;

/// <summary>Business time (Nepal by default). The server itself may run in UTC.</summary>
public sealed class AppClock
{
    private readonly TimeZoneInfo _tz;

    public AppClock(IOptions<AppOptions> options, ILogger<AppClock> logger)
    {
        var id = string.IsNullOrWhiteSpace(options.Value.TimeZone) ? "Asia/Kathmandu" : options.Value.TimeZone;
        try
        {
            _tz = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning("Time zone {Id} not found on this machine; using fixed UTC+05:45.", id);
            _tz = TimeZoneInfo.CreateCustomTimeZone("NPT", TimeSpan.FromMinutes(345), "Nepal Time", "Nepal Time");
        }
    }

    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz);

    public DateOnly Today => DateOnly.FromDateTime(Now);

    public static string TimeText(DateTime t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public static string StampText(DateTime t) => t.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
