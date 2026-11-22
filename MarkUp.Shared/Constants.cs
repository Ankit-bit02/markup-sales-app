namespace MarkUp.Shared;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Sales = "Sales";
    public const string Bill = "Bill";

    public const string AdminOrBill = Admin + "," + Bill;

    public static readonly string[] All = { Admin, Sales, Bill };

    public static bool IsValid(string? role) => role is Admin or Sales or Bill;
}

public static class OrderStatus
{
    public const string Pending = "Pending";
    public const string Billed = "Billed";
    public const string Cancelled = "Cancelled";
}

public static class SchemeTypes
{
    public const string Percent = "Percent";
    public const string Flat = "Flat";
    public const string FreeQty = "FreeQty";

    public static readonly string[] All = { Percent, Flat, FreeQty };

    public static bool IsValid(string? type) => type is Percent or Flat or FreeQty;

    public static string Label(string? type) => type switch
    {
        Percent => "Percent discount",
        Flat => "Flat discount",
        FreeQty => "Free quantity",
        _ => type ?? ""
    };
}

public static class UnitKinds
{
    /// <summary>Base unit of the product (e.g. pcs). Rates are per base unit.</summary>
    public const string Unit = "Unit";

    /// <summary>Second (larger) unit of the product (e.g. box = ConvFactor base units).</summary>
    public const string Second = "Second";

    public static bool IsValid(string? unit) => unit is Unit or Second;
}

public static class ReportKinds
{
    public const string PartyList = "party-list";
    public const string Dsr = "dsr";
    public const string Products = "products";
    public const string TargetVsAch = "target-vs-ach";
    public const string ProductTarget = "product-target";
    public const string Attendance = "attendance";
}

public static class TargetViews
{
    public const string Daily = "daily";
    public const string Mtd = "mtd";
    public const string Monthly = "monthly";
    public const string Overall = "overall";
}

public static class ProfileOptions
{
    public static readonly string[] Sexes = { "Male", "Female", "Other" };
    public static readonly string[] MaritalStatuses = { "Single", "Married", "Divorced" };

    public const int MaxPhotoChars = 45000;
}


/// <summary>Distance and "is the salesperson at the shop" rule, shared by the phone and the server.</summary>
public static class Geo
{
    public static double DistanceM(double lat1, double lng1, double lat2, double lng2)
    {
        const double r = 6371000.0;
        static double Rad(double d) => d * Math.PI / 180.0;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>
    /// Null when the visit may be saved; otherwise the message to show.
    /// radiusM = 0 switches the check off. Accuracy 0 means "unknown" (older app versions).
    /// A shop without GPS passes: its first visit saves the location.
    /// </summary>
    public static string? CheckAtShop(double lat, double lng, double accuracy, double? shopLat, double? shopLng,
                                      string shopName, int radiusM, int maxAccuracyM)
    {
        if (radiusM <= 0) return null;
        if (accuracy > maxAccuracyM)
            return $"Your GPS is not accurate enough (about {accuracy:0} m; {maxAccuracyM} m or better is needed). " +
                   "Step outside near the shop, wait a few seconds and try again.";
        if (shopLat is not { } sLat || shopLng is not { } sLng) return null;
        var d = DistanceM(lat, lng, sLat, sLng);
        return d > radiusM
            ? $"You are {d:0} m from {shopName}. Visits can only be saved within {radiusM} m of the shop. Go to the shop and try again."
            : null;
    }
}