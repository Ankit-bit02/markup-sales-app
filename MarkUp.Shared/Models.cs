using System.ComponentModel.DataAnnotations;

namespace MarkUp.Shared;

/// <summary>Every stored record has a string Id (one Google Sheets row).</summary>
public interface IEntity
{
    string Id { get; set; }
}

/// <summary>Master records are never deleted, only deactivated.</summary>
public interface IActivatable
{
    bool IsActive { get; set; }
}

// ---------------------------------------------------------------- Master settings

public class PartyType : IEntity, IActivatable
{
    public string Id { get; set; } = "";

    [Required, StringLength(60, MinimumLength = 2)]
    public string Name { get; set; } = "";

    public bool IsActive { get; set; } = true;
}

public class Holiday : IEntity, IActivatable
{
    public string Id { get; set; } = "";

    [Required]
    public DateOnly Date { get; set; }

    [Required, StringLength(80, MinimumLength = 2)]
    public string Name { get; set; } = "";

    public bool IsActive { get; set; } = true;
}

/// <summary>Weekly off days for a year, e.g. "Saturday" or "Saturday,Sunday".</summary>
public class CalendarSetting : IEntity, IActivatable
{
    public string Id { get; set; } = "";

    [Range(2020, 2100)]
    public int Year { get; set; }

    [StringLength(100)]
    public string WeeklyOffDays { get; set; } = "Saturday";

    public bool IsActive { get; set; } = true;
}

// ---------------------------------------------------------------- Masters

public class Dealer : IEntity, IActivatable
{
    public string Id { get; set; } = "";

    [Required, StringLength(120, MinimumLength = 2)]
    public string Name { get; set; } = "";

    [StringLength(80)]
    public string ContactPerson { get; set; } = "";

    [StringLength(20)]
    [RegularExpression(@"^[0-9+\-\s]*$", ErrorMessage = "Phone can contain digits, spaces, + and - only.")]
    public string Phone { get; set; } = "";

    [StringLength(200)]
    public string Address { get; set; } = "";

    public bool IsActive { get; set; } = true;
}

public class Beat : IEntity, IActivatable
{
    public string Id { get; set; } = "";

    [Required, StringLength(120, MinimumLength = 2)]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Select a dealer.")]
    public string DealerId { get; set; } = "";

    [Required(ErrorMessage = "Select the salesperson for this beat.")]
    public string AssignedUserId { get; set; } = "";

    public bool IsActive { get; set; } = true;
}

public class Party : IEntity, IActivatable
{
    public string Id { get; set; } = "";

    [Required, StringLength(150, MinimumLength = 2)]
    public string Name { get; set; } = "";

    [StringLength(80)]
    public string ContactPerson { get; set; } = "";

    [StringLength(20)]
    [RegularExpression(@"^[0-9+\-\s]*$", ErrorMessage = "Phone can contain digits, spaces, + and - only.")]
    public string Phone { get; set; } = "";

    [StringLength(200)]
    public string Address { get; set; } = "";

    [Required(ErrorMessage = "Select a party type.")]
    public string PartyTypeId { get; set; } = "";

    [Required(ErrorMessage = "Select a beat.")]
    public string BeatId { get; set; } = "";

    /// <summary>Shop GPS. Saved from the first visit; Admin can correct it. Empty = unknown.</summary>
    [Range(-90, 90, ErrorMessage = "Latitude must be between -90 and 90.")]
    public double? Lat { get; set; }

    [Range(-180, 180, ErrorMessage = "Longitude must be between -180 and 180.")]
    public double? Lng { get; set; }


    /// <summary>Salesperson who added the shop from the phone. Empty = added by the office.</summary>
    [StringLength(40)]
    public string AddedByUserId { get; set; } = "";

    public DateTime? AddedAt { get; set; }

    /// <summary>True for shops added by a salesperson until an administrator marks them as checked.</summary>
    public bool NeedsReview { get; set; }

    public bool IsActive { get; set; } = true;
}

public class Brand : IEntity, IActivatable
{
    public string Id { get; set; } = "";

    /// <summary>Short code, e.g. "BM". Stored in capitals.</summary>
    [Required(ErrorMessage = "Enter a brand code."), StringLength(10, MinimumLength = 1)]
    [RegularExpression(@"^[A-Za-z0-9\-]+$", ErrorMessage = "Brand code can contain letters, numbers and - only.")]
    public string Code { get; set; } = "";

    [Required(ErrorMessage = "Enter the brand name."), StringLength(80, MinimumLength = 2)]
    public string Name { get; set; } = "";

    [StringLength(300)]
    public string Description { get; set; } = "";

    public bool IsActive { get; set; } = true;
}
public class Product : IEntity, IActivatable
{
    public string Id { get; set; } = "";


    /// <summary>The brand (category) this product belongs to.</summary>
    [Required(ErrorMessage = "Select a brand."), StringLength(40)]
    public string BrandId { get; set; } = "";

    [Required, StringLength(30, MinimumLength = 1)]
    public string Code { get; set; } = "";

    [StringLength(80)]
    public string GroupName { get; set; } = "";

    [Required, StringLength(150, MinimumLength = 2)]
    public string Name { get; set; } = "";

    /// <summary>Base unit, e.g. "Pcs". Rates are per base unit.</summary>
    [Required, StringLength(20)]
    public string Unit { get; set; } = "Pcs";

    /// <summary>Larger unit, e.g. "Box". Optional.</summary>
    [StringLength(20)]
    public string SecondUnit { get; set; } = "";

    /// <summary>How many base units make one second unit (e.g. 12).</summary>
    [Range(typeof(decimal), "1", "100000", ParseLimitsInInvariantCulture = true)]
    public decimal ConvFactor { get; set; } = 1;

    [Range(typeof(decimal), "0", "10000000", ParseLimitsInInvariantCulture = true)]
    public decimal MrpNpr { get; set; }

    [Range(typeof(decimal), "0", "10000000", ParseLimitsInInvariantCulture = true)]
    public decimal MrpInr { get; set; }

    [Range(typeof(decimal), "0", "10000000", ParseLimitsInInvariantCulture = true)]
    public decimal DealerRate { get; set; }

    [Range(typeof(decimal), "0", "10000000", ParseLimitsInInvariantCulture = true)]
    public decimal RetailerRate { get; set; }

    public bool IsActive { get; set; } = true;
}

public class Scheme : IEntity, IActivatable
{
    public string Id { get; set; } = "";

    [Required, StringLength(100, MinimumLength = 2)]
    public string Name { get; set; } = "";

    [StringLength(300)]
    public string Description { get; set; } = "";

    [Required(ErrorMessage = "Select a product.")]
    public string ProductId { get; set; } = "";

    /// <summary>Percent, Flat or FreeQty (see SchemeTypes).</summary>
    [Required]
    public string Type { get; set; } = SchemeTypes.Percent;

    /// <summary>Percent (0-100), flat amount (NPR) or free quantity (base units) depending on Type.</summary>
    [Range(typeof(decimal), "0", "10000000", ParseLimitsInInvariantCulture = true)]
    public decimal Value { get; set; }

    /// <summary>Minimum quantity (base units) on the order line for the scheme to apply.</summary>
    [Range(typeof(decimal), "0", "10000000", ParseLimitsInInvariantCulture = true)]
    public decimal MinQty { get; set; }

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public bool IsActive { get; set; } = true;
}

public class Target : IEntity, IActivatable
{
    public string Id { get; set; } = "";

    [Required(ErrorMessage = "Select a salesperson.")]
    public string UserId { get; set; } = "";

    [Range(2020, 2100)]
    public int Year { get; set; }

    [Range(1, 12)]
    public int Month { get; set; }

    [Range(typeof(decimal), "0", "1000000000", ParseLimitsInInvariantCulture = true)]
    public decimal TargetValue { get; set; }

    public bool IsActive { get; set; } = true;
}

public class ProductTarget : IEntity, IActivatable
{
    public string Id { get; set; } = "";

    [Required(ErrorMessage = "Select a salesperson.")]
    public string UserId { get; set; } = "";

    [Range(2020, 2100)]
    public int Year { get; set; }

    [Range(1, 12)]
    public int Month { get; set; }

    [Required(ErrorMessage = "Select a product.")]
    public string ProductId { get; set; } = "";

    /// <summary>Target quantity in base units.</summary>
    [Range(typeof(decimal), "0", "100000000", ParseLimitsInInvariantCulture = true)]
    public decimal TargetQty { get; set; }

    public bool IsActive { get; set; } = true;
}

// ---------------------------------------------------------------- Transactions

public class Attendance : IEntity
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Day { get; set; } = "";
    public string LoginTime { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string LocationName { get; set; } = "";
    /// <summary>True when the user refused location (billing staff only): time is recorded, Lat/Lng are 0.</summary>
    public bool NoGps { get; set; }
    public DateTime SyncedAt { get; set; }
}

public class Visit : IEntity
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Day { get; set; } = "";
    public string Time { get; set; } = "";
    public string BeatId { get; set; } = "";
    public string PartyId { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string LocationName { get; set; } = "";
    public bool IsProductive { get; set; }
    public string NoOrderReason { get; set; } = "";
    public DateTime SyncedAt { get; set; }

    /// <summary>Distance in metres from the shop's saved GPS when the visit was saved (empty = shop had no GPS yet).</summary>
    public int? DistanceM { get; set; }

    /// <summary>GPS accuracy in metres reported by the phone (empty = not reported).</summary>
    public int? GpsAccuracyM { get; set; }
}



/// <summary>App-wide settings (one row, Id = "main"), edited in Master Settings.</summary>
public class AppSettings : IEntity
{
    public const string MainId = "main";

    public string Id { get; set; } = MainId;

    /// <summary>Visits and new shops are only allowed within this distance of the shop. 0 = check off.</summary>
    [Range(0, 5000, ErrorMessage = "The radius must be between 0 and 5,000 metres.")]
    public int VisitRadiusM { get; set; } = 100;

    /// <summary>The phone's GPS reading must be at least this accurate (metres).</summary>
    [Range(10, 500, ErrorMessage = "GPS accuracy must be between 10 and 500 metres.")]
    public int MaxGpsAccuracyM { get; set; } = 50;
}

public class Order : IEntity
{
    public string Id { get; set; } = "";
    public string OrderNo { get; set; } = "";
    public string VisitId { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Time { get; set; } = "";
    public string UserId { get; set; } = "";
    public string DealerId { get; set; } = "";
    public string BeatId { get; set; } = "";
    public string PartyId { get; set; } = "";
    public decimal TotalValue { get; set; }
    public string Status { get; set; } = OrderStatus.Pending;
    public string CancelReason { get; set; } = "";
    public string BilledBy { get; set; } = "";
    public string BilledAt { get; set; } = "";
    public string CancelledBy { get; set; } = "";
    public string CancelledAt { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string LocationName { get; set; } = "";
    public DateTime SyncedAt { get; set; }
}

public class OrderItem : IEntity
{
    public string Id { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public decimal Qty { get; set; }
    public string Unit { get; set; } = UnitKinds.Unit;
    public decimal BaseQty { get; set; }
    public decimal Rate { get; set; }
    public decimal Gross { get; set; }
    public string SchemeId { get; set; } = "";
    public decimal Discount { get; set; }
    public decimal FreeQty { get; set; }
    public decimal LineValue { get; set; }
}
