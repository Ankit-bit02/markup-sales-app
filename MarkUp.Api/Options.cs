namespace MarkUp.Api;

public class GoogleSheetsOptions
{
    public string SpreadsheetId { get; set; } = "";

    /// <summary>Path to the service account JSON key file (local development).</summary>
    public string CredentialsPath { get; set; } = "";

    /// <summary>The whole service account JSON as text (hosting, e.g. Render env var).</summary>
    public string CredentialsJson { get; set; } = "";

    /// <summary>The service account JSON, base64-encoded (alternative for hosts that mangle JSON env vars).</summary>
    public string CredentialsBase64 { get; set; } = "";

    /// <summary>How long a tab stays cached in memory before it is re-read from Google.</summary>
    public int CacheMinutes { get; set; } = 10;

    /// <summary>Adds a warning-only protection to each data tab so people editing by hand get a warning.</summary>
    public bool ProtectTabs { get; set; } = true;
}

public class JwtOptions
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "markup-api";
    public string Audience { get; set; } = "markup-app";
    public int ExpiryHours { get; set; } = 12;
}

public class AppOptions
{
    public string TimeZone { get; set; } = "Asia/Kathmandu";
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
    public bool ReverseGeocoding { get; set; } = true;
    public string GeocodingUserAgent { get; set; } = "MarkUpSalesApp/1.0";
    public int MaxLoginAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    /// <summary>How many days back an offline record may be dated when it syncs.</summary>
    public int MaxOfflineDays { get; set; } = 7;

    /// <summary>Location tracking points older than this are deleted automatically.</summary>
    public int TrackingRetentionDays { get; set; } = 90;
}

public class SeedOptions
{
    public string AdminUsername { get; set; } = "";
    public string AdminPassword { get; set; } = "";
    public string AdminName { get; set; } = "Administrator";
}
