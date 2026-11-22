using System.Text;
using System.Threading.RateLimiting;
using MarkUp.Api;
using MarkUp.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Hosts like Render pass the port in PORT.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 1 * 1024 * 1024; // 1 MB is plenty for any request this API accepts
    k.AddServerHeader = false;
});

// ------------------------------------------------------------------ Options (validated at start)

builder.Services.Configure<GoogleSheetsOptions>(builder.Configuration.GetSection("GoogleSheets"));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection("Seed"));

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
var app0 = builder.Configuration.GetSection("App").Get<AppOptions>() ?? new AppOptions();
var sheets0 = builder.Configuration.GetSection("GoogleSheets").Get<GoogleSheetsOptions>() ?? new GoogleSheetsOptions();

if (string.IsNullOrWhiteSpace(jwt.Key) || Encoding.UTF8.GetByteCount(jwt.Key) < 32)
    throw new InvalidOperationException("Jwt:Key must be set and at least 32 characters long.");
if (string.IsNullOrWhiteSpace(sheets0.SpreadsheetId))
    throw new InvalidOperationException("GoogleSheets:SpreadsheetId must be set.");

// ------------------------------------------------------------------ Services

builder.Services.AddSingleton<SheetStore>();
builder.Services.AddSingleton<AppClock>();
builder.Services.AddSingleton<GeocodingService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<MasterData>();
builder.Services.AddSingleton<SalesService>();
builder.Services.AddSingleton<BillingService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<ReportExporter>();
builder.Services.AddSingleton<TrackingService>();
builder.Services.AddHostedService<TrackingCleanupService>();

builder.Services.AddHttpClient(GeocodingService.HttpClientName, c =>
{
    c.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
    c.Timeout = TimeSpan.FromSeconds(5);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(app0.GeocodingUserAgent) ? "MarkUpSalesApp/1.0" : app0.GeocodingUserAgent);
});

builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(o =>
    {
        // Return validation errors as one readable message.
        o.InvalidModelStateResponseFactory = ctx =>
        {
            var messages = ctx.ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Invalid value." : e.ErrorMessage)
                .Distinct();
            return new BadRequestObjectResult(new ApiError { Message = string.Join(" ", messages) });
        };
    });

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = ClaimNames.Name,
            RoleClaimType = ClaimNames.Role
        };
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var auth = ctx.HttpContext.RequestServices.GetRequiredService<AuthService>();
                if (ctx.Principal == null || !await auth.IsTokenStillValidAsync(ctx.Principal))
                {
                    ctx.Fail("Session is no longer valid.");
                }
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddCors(o => o.AddPolicy("app", p =>
{
    var origins = app0.AllowedOrigins.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.TrimEnd('/')).ToArray();
    if (origins.Length > 0)
    {
        p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition");
    }
}));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.ContentType = "application/json";
        await ctx.HttpContext.Response.WriteAsJsonAsync(new ApiError { Message = "Too many requests. Please wait a moment." }, ct);
    };

    // Login: 10 attempts per minute per IP.
    o.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    // Everything else: 300 requests per minute per user (or IP when anonymous).
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        var key = http.User.FindFirst(ClaimNames.UserId)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    });
});

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var app = builder.Build();

// ------------------------------------------------------------------ Pipeline

app.UseForwardedHeaders();

app.Use(async (ctx, next) =>
{
    // Added when the response starts so they survive error responses too.
    ctx.Response.OnStarting(() =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        h["Cache-Control"] = "no-store";
        return Task.CompletedTask;
    });
    await next();
});

app.UseMiddleware<ErrorHandlingMiddleware>();

app.UseCors("app");
app.UseAuthentication();

// Users who must change their password can only use the auth endpoints.
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true
        && ctx.User.FindFirst(ClaimNames.MustChangePassword)?.Value == "1"
        && !ctx.Request.Path.StartsWithSegments("/api/auth"))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new ApiError { Message = "You must change your password first." });
        return;
    }
    await next();
});

app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapControllers();

// ------------------------------------------------------------------ Startup: create tabs, seed first admin

using (var scope = app.Services.CreateScope())
{
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var store = scope.ServiceProvider.GetRequiredService<SheetStore>();
    var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
    var seed = builder.Configuration.GetSection("Seed").Get<SeedOptions>() ?? new SeedOptions();

    log.LogInformation("Checking Google Sheet structure...");
    await store.EnsureSchemaAsync();
    await auth.SeedAdminAsync(seed);
    log.LogInformation("Google Sheet ready.");
}

app.Run();
