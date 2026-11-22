using System.ComponentModel.DataAnnotations;

namespace MarkUp.Shared;

// ---------------------------------------------------------------- Auth

public class LoginRequest
{
    [Required, StringLength(50)]
    public string Username { get; set; } = "";

    [Required, StringLength(100)]
    public string Password { get; set; } = "";
}

public class LoginResponse
{
    public string Token { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public bool MustChangePassword { get; set; }
}

public class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = "";

    [Required, StringLength(100, MinimumLength = 8, ErrorMessage = "New password must be at least 8 characters.")]
    public string NewPassword { get; set; } = "";
}

// ---------------------------------------------------------------- Users

public class UserDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Role { get; set; } = Roles.Sales;
    public string DealerId { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class UserSaveRequest
{
    [Required, StringLength(100, MinimumLength = 2)]
    public string Name { get; set; } = "";

    [Required, StringLength(50, MinimumLength = 3)]
    [RegularExpression(@"^[a-zA-Z0-9._-]+$", ErrorMessage = "Username can contain letters, digits, dot, dash and underscore only.")]
    public string Username { get; set; } = "";

    [StringLength(20)]
    [RegularExpression(@"^[0-9+\-\s]*$", ErrorMessage = "Phone can contain digits, spaces, + and - only.")]
    public string Phone { get; set; } = "";

    [Required]
    public string Role { get; set; } = Roles.Sales;

    public string DealerId { get; set; } = "";

    public bool IsActive { get; set; } = true;

    /// <summary>Required when creating. When updating, leave empty to keep the current password.</summary>
    [StringLength(100)]
    public string Password { get; set; } = "";
}

// ---------------------------------------------------------------- Lookups

public class LookupItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class LookupsResponse
{
    public List<LookupItem> SalesUsers { get; set; } = new();
    /// <summary>Active billing staff (Admin only; empty for others). Used by the Attendance report.</summary>
    public List<LookupItem> BillUsers { get; set; } = new();
    public List<LookupItem> Dealers { get; set; } = new();
    public List<LookupItem> Beats { get; set; } = new();
    public List<LookupItem> Products { get; set; } = new();
}

// ---------------------------------------------------------------- Sales

public class SalesBootstrap
{
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string DealerName { get; set; } = "";
    public DateOnly Today { get; set; }
    public bool AttendanceMarkedToday { get; set; }
    public string AttendanceTime { get; set; } = "";
    public string AttendanceLocation { get; set; } = "";
    public List<Beat> Beats { get; set; } = new();
    public List<Party> Parties { get; set; } = new();
    public List<Brand> Brands { get; set; } = new();
    public List<Product> Products { get; set; } = new();
    public List<Scheme> Schemes { get; set; } = new();
    public List<PartyType> PartyTypes { get; set; } = new();
    /// <summary>Location check from Master Settings (so the phone can check offline too).</summary>
    public int VisitRadiusM { get; set; } = 100;
    public int MaxGpsAccuracyM { get; set; } = 50;
    public DateTime LoadedAtUtc { get; set; }
}

public class AttendanceRequest
{
    /// <summary>Client-generated GUID so a retried sync never creates a duplicate.</summary>
    [Required, StringLength(64)]
    public string Id { get; set; } = "";

    /// <summary>Local (Nepal) time on the device when attendance was captured.</summary>
    public DateTime CapturedAt { get; set; }

    [Range(-90, 90)]
    public double Lat { get; set; }

    [Range(-180, 180)]
    public double Lng { get; set; }
    /// <summary>Billing staff only: location was refused or unavailable; the time is still recorded.</summary>
    public bool NoGps { get; set; }
}

public class AttendanceResult
{
    public string Id { get; set; } = "";
    public DateOnly Date { get; set; }
    public string LoginTime { get; set; } = "";
    public string LocationName { get; set; } = "";
    public bool NoGps { get; set; }
    public bool AlreadyMarked { get; set; }
}

/// <summary>One person's row on the daily attendance page.</summary>
public class AttendanceRow
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public bool SignedIn { get; set; }
    public string LoginTime { get; set; } = "";
    public string LocationName { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public bool NoGps { get; set; }
}

public class DailyAttendance
{
    public DateOnly Date { get; set; }
    public string Day { get; set; } = "";
    public int Total { get; set; }
    public int SignedIn { get; set; }
    public List<AttendanceRow> Rows { get; set; } = new();
}

public class OrderLineRequest
{
    [Required]
    public string ProductId { get; set; } = "";

    [Range(typeof(decimal), "0.001", "1000000", ParseLimitsInInvariantCulture = true)]
    public decimal Qty { get; set; }

    [Required]
    public string Unit { get; set; } = UnitKinds.Unit;

    public string SchemeId { get; set; } = "";
}

public class VisitRequest
{
    /// <summary>Client-generated GUID so a retried sync never creates a duplicate.</summary>
    [Required, StringLength(64)]
    public string Id { get; set; } = "";

    /// <summary>Local (Nepal) time on the device when the visit was saved.</summary>
    public DateTime CapturedAt { get; set; }

    [Required]
    public string BeatId { get; set; } = "";

    [Required]
    public string PartyId { get; set; } = "";

    [Range(-90, 90)]
    public double Lat { get; set; }

    [Range(-180, 180)]
    public double Lng { get; set; }

    /// <summary>GPS accuracy in metres reported by the phone (0 = unknown).</summary>
    [Range(0, 100000)]
    public double Accuracy { get; set; }

    [StringLength(200)]
    public string NoOrderReason { get; set; } = "";

    public List<OrderLineRequest> Items { get; set; } = new();
}

public class VisitResult
{
    public string VisitId { get; set; } = "";
    public bool Productive { get; set; }
    public string OrderId { get; set; } = "";
    public string OrderNo { get; set; } = "";
    public string PartyName { get; set; } = "";
    public decimal TotalValue { get; set; }
    public string LocationName { get; set; } = "";
    public string WhatsAppText { get; set; } = "";
    public bool AlreadySynced { get; set; }
}

// ---------------------------------------------------------------- Billing

public class OrderItemView
{
    public string ProductCode { get; set; } = "";
    public string ProductName { get; set; } = "";
    public decimal Qty { get; set; }
    public string UnitName { get; set; } = "";
    public decimal Rate { get; set; }
    public decimal Discount { get; set; }
    public decimal FreeQty { get; set; }
    public decimal LineValue { get; set; }
    public string SchemeName { get; set; } = "";
}

public class OrderView
{
    public string Id { get; set; } = "";
    public string OrderNo { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Time { get; set; } = "";
    public string Status { get; set; } = "";
    public string PartyName { get; set; } = "";
    public string PartyAddress { get; set; } = "";
    public string PartyContactPerson { get; set; } = "";
    public string PartyPhone { get; set; } = "";
    public string SalesPersonName { get; set; } = "";
    public string BeatName { get; set; } = "";
    public string DealerName { get; set; } = "";
    public decimal TotalValue { get; set; }
    public string CancelReason { get; set; } = "";
    public string BilledByName { get; set; } = "";
    public string BilledAt { get; set; } = "";
    public string CancelledByName { get; set; } = "";
    public string CancelledAt { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string LocationName { get; set; } = "";
    public List<OrderItemView> Items { get; set; } = new();
}

public class CancelOrderRequest
{
    [Required(ErrorMessage = "A cancellation reason is required.")]
    [StringLength(200, MinimumLength = 3, ErrorMessage = "Reason must be 3 to 200 characters.")]
    public string Reason { get; set; } = "";
}

// ---------------------------------------------------------------- Reports

public class ReportField
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";

    public ReportField() { }

    public ReportField(string label, string value)
    {
        Label = label;
        Value = value;
    }
}

/// <summary>Generic tabular report used by the screen and by Excel/PDF export.</summary>
public class ReportTable
{
    public string Title { get; set; } = "";
    public List<ReportField> Header { get; set; } = new();
    public List<string> Columns { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
    public List<ReportField> Footer { get; set; } = new();

    /// <summary>Indexes of columns holding numbers (right-aligned, exported as numbers to Excel).</summary>
    public List<int> NumericColumns { get; set; } = new();
}

public class ApiError
{
    public string Message { get; set; } = "";
}



// ---------------------------------------------------------------- Profile

public class ProfileView
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";
    public string DealerName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }
    public int? Age { get; set; }
    public string Sex { get; set; } = "";
    public string MaritalStatus { get; set; } = "";
    public string Address { get; set; } = "";
    public string EmergencyPhone { get; set; } = "";

    /// <summary>Profile picture as a data URL (data:image/jpeg;base64,...), or empty.</summary>
    public string Photo { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public class ProfileSaveRequest
{
    [StringLength(20)]
    [RegularExpression(@"^[0-9+\-\s]*$", ErrorMessage = "Contact number can contain digits, spaces, + and - only.")]
    public string Phone { get; set; } = "";

    [StringLength(100)]
    [RegularExpression(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", ErrorMessage = "Enter a valid email address.")]
    public string Email { get; set; } = "";

    public DateOnly? DateOfBirth { get; set; }

    [StringLength(10)]
    public string Sex { get; set; } = "";

    [StringLength(20)]
    public string MaritalStatus { get; set; } = "";

    [StringLength(300)]
    public string Address { get; set; } = "";

    [StringLength(20)]
    [RegularExpression(@"^[0-9+\-\s]*$", ErrorMessage = "Emergency contact can contain digits, spaces, + and - only.")]
    public string EmergencyPhone { get; set; } = "";

    [StringLength(ProfileOptions.MaxPhotoChars, ErrorMessage = "The profile picture is too large. Choose a smaller picture.")]
    public string Photo { get; set; } = "";
}



// ---------------------------------------------------------------- Product import

public static class ImportActions
{
    public const string New = "New";
    public const string Update = "Update";
    public const string Unchanged = "Unchanged";
    public const string Error = "Error";
}

public class ProductImportRow
{
    /// <summary>Row number in the uploaded file (as Excel shows it).</summary>
    public int RowNumber { get; set; }
    public string Action { get; set; } = "";
    public string Message { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string Unit { get; set; } = "";
    public string SecondUnit { get; set; } = "";
    public decimal ConvFactor { get; set; }
    public decimal MrpNpr { get; set; }
    public decimal RetailerRate { get; set; }
}

public class ProductImportResult
{
    /// <summary>False = preview only (nothing saved). True = rows were saved.</summary>
    public bool Applied { get; set; }
    public string FileName { get; set; } = "";
    public string BrandName { get; set; } = "";
    public int NewCount { get; set; }
    public int UpdateCount { get; set; }
    public int UnchangedCount { get; set; }
    public int ErrorCount { get; set; }

    /// <summary>Which file columns were recognised, e.g. "Name → Product name".</summary>
    public List<string> MappedColumns { get; set; } = new();
    public List<ProductImportRow> Rows { get; set; } = new();
}


// ---------------------------------------------------------------- Location tracking

public static class TrackKinds
{
    public const string Start = "Start";
    public const string Point = "Point";
    public const string End = "End";

    public static bool IsValid(string? k) => k is Start or Point or End;
}

public class TrackPointRequest
{
    /// <summary>Client-generated id, echoed back so the phone can remove sent points.</summary>
    [Required, StringLength(64)]
    public string Id { get; set; } = "";

    [Required, StringLength(10)]
    public string Kind { get; set; } = TrackKinds.Point;

    /// <summary>Local (Nepal) time on the phone when the point was taken.</summary>
    public DateTime CapturedAt { get; set; }

    public double Lat { get; set; }
    public double Lng { get; set; }

    /// <summary>GPS accuracy in metres (smaller is better).</summary>
    public double Accuracy { get; set; }

    [StringLength(40)]
    public string BeatId { get; set; } = "";
}

public class TrackUploadRequest
{
    [Required, MaxLength(500)]
    public List<TrackPointRequest> Points { get; set; } = new();
}

public class TrackUploadResult
{
    /// <summary>Ids the server has handled (saved, or dropped as invalid). The phone removes these.</summary>
    public List<string> DoneIds { get; set; } = new();
    public int Saved { get; set; }
    public int Dropped { get; set; }
}

public class LiveLocation
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string BeatNames { get; set; } = "";
    public bool HasLocation { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double Accuracy { get; set; }
    public string LastSeen { get; set; } = "";
    public int MinutesAgo { get; set; }

    /// <summary>Started the beat and sent a point in the last 10 minutes.</summary>
    public bool IsTracking { get; set; }
    public bool AttendanceToday { get; set; }
    public int VisitsToday { get; set; }
    public int OrdersToday { get; set; }
}

public class RoutePoint
{
    public string Time { get; set; } = "";
    public string Kind { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double Accuracy { get; set; }
}

public class RouteStop
{
    public string Time { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string PartyName { get; set; } = "";
    public bool IsProductive { get; set; }
    public string OrderNo { get; set; } = "";
    public decimal OrderValue { get; set; }
    public string NoOrderReason { get; set; } = "";
}

public class RouteParty
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string BeatName { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public bool Visited { get; set; }
}

public class RouteView
{
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public DateOnly Date { get; set; }
    public string BeatNames { get; set; } = "";
    public string AttendanceTime { get; set; } = "";
    public double? AttendanceLat { get; set; }
    public double? AttendanceLng { get; set; }
    public List<RoutePoint> Points { get; set; } = new();
    public List<RouteStop> Stops { get; set; } = new();
    public List<RouteParty> Parties { get; set; } = new();
    public decimal DistanceKm { get; set; }
    public string FirstTime { get; set; } = "";
    public string LastTime { get; set; } = "";

    /// <summary>Minutes between consecutive points that are close enough to count as tracked (gaps excluded).</summary>
    public int TrackedMinutes { get; set; }
}


// ---------------------------------------------------------------- New party from the phone

/// <summary>A shop added by a salesperson in the field. Usable at once; the office reviews it later.</summary>
public class NewPartyRequest
{
    /// <summary>Created on the phone, so the shop can be used offline and sending twice is harmless.</summary>
    [Required, StringLength(64, MinimumLength = 8)]
    [RegularExpression(@"^[A-Za-z0-9\-]+$")]
    public string Id { get; set; } = "";

    [Required(ErrorMessage = "Enter the shop name."), StringLength(150, MinimumLength = 2, ErrorMessage = "Shop name must be 2 to 150 characters.")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Select the party type.")]
    public string PartyTypeId { get; set; } = "";

    [Required(ErrorMessage = "Select the beat.")]
    public string BeatId { get; set; } = "";

    [StringLength(80)]
    public string ContactPerson { get; set; } = "";

    [StringLength(20)]
    [RegularExpression(@"^[0-9+\-\s]*$", ErrorMessage = "Phone can contain digits, spaces, + and - only.")]
    public string Phone { get; set; } = "";

    [StringLength(200)]
    public string Address { get; set; } = "";

    /// <summary>Where the salesperson stood when adding the shop (optional).</summary>
    public double? Lat { get; set; }
    public double? Lng { get; set; }

    /// <summary>GPS accuracy in metres reported by the phone (0 = unknown).</summary>
    public double Accuracy { get; set; }
}