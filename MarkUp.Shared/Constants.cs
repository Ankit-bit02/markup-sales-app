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