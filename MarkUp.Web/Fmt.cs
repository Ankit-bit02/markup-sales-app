using System.Globalization;

namespace MarkUp.Web;

/// <summary>Display formatting that does not depend on the phone's language settings.</summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Money(decimal v) => v.ToString("#,##0.00", Inv);

    public static string Qty(decimal v) => v.ToString("0.###", Inv);

    public static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    /// <summary>Phone's local time without a time-zone marker (the server treats it as Nepal time).</summary>
    public static DateTime LocalNow() => DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified);

    public static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    public static string MapLink(double lat, double lng) =>
        string.Create(Inv, $"https://maps.google.com/?q={lat:0.000000},{lng:0.000000}");
}
