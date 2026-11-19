using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using MarkUp.Shared;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MarkUp.Api;

public static class ClaimNames
{
    public const string UserId = "sub";
    public const string Name = "name";
    public const string Role = "role";
    public const string Username = "username";
    public const string Stamp = "stamp";
    public const string MustChangePassword = "pwd_change";
}

public static class PrincipalExtensions
{
    public static string UserId(this ClaimsPrincipal user) =>
        user.FindFirst(ClaimNames.UserId)?.Value ?? throw AppException.Forbidden("Not signed in.");

    public static string Role(this ClaimsPrincipal user) => user.FindFirst(ClaimNames.Role)?.Value ?? "";
}

public sealed class AuthService
{
    // Pre-computed hash used to keep login timing the same whether or not the username exists.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy-password-for-timing", 11);

    private readonly SheetStore _store;
    private readonly AppClock _clock;
    private readonly JwtOptions _jwt;
    private readonly AppOptions _app;
    private readonly ILogger<AuthService> _logger;
    private readonly ConcurrentDictionary<string, (int Failures, DateTime LockedUntilUtc)> _failures = new();

    public AuthService(SheetStore store, AppClock clock, IOptions<JwtOptions> jwt, IOptions<AppOptions> app, ILogger<AuthService> logger)
    {
        _store = store;
        _clock = clock;
        _jwt = jwt.Value;
        _app = app.Value;
        _logger = logger;
    }

    public static string HashPassword(string password) => BCrypt.Net.BCrypt.HashPassword(password, 11);

    public static void ValidatePasswordStrength(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            throw AppException.BadRequest("Password must be at least 8 characters.");
        if (password.Length > 100)
            throw AppException.BadRequest("Password is too long.");
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw AppException.BadRequest("Password must contain at least one letter and one number.");
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest req)
    {
        var username = (req.Username ?? "").Trim().ToLowerInvariant();
        var key = username;

        if (_failures.TryGetValue(key, out var state) && state.LockedUntilUtc > DateTime.UtcNow)
        {
            var mins = Math.Ceiling((state.LockedUntilUtc - DateTime.UtcNow).TotalMinutes);
            throw new AppException(StatusCodes.Status429TooManyRequests,
                $"Too many failed attempts. Try again in {mins} minute(s).");
        }

        var users = await _store.GetAllAsync<UserRecord>();
        var user = users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

        var valid = user != null
            ? BCrypt.Net.BCrypt.Verify(req.Password ?? "", user.PasswordHash)
            : BCrypt.Net.BCrypt.Verify(req.Password ?? "", DummyHash) && false;

        if (!valid || user == null || !user.IsActive)
        {
            RegisterFailure(key);
            _logger.LogWarning("Failed login for username {Username}.", username);
            throw new AppException(StatusCodes.Status401Unauthorized, "Invalid username or password.");
        }

        _failures.TryRemove(key, out _);
        return CreateToken(user);
    }

    private void RegisterFailure(string key)
    {
        var max = Math.Max(1, _app.MaxLoginAttempts);
        _failures.AddOrUpdate(key,
            _ => max <= 1
                ? (0, DateTime.UtcNow.AddMinutes(Math.Max(1, _app.LockoutMinutes)))
                : (1, DateTime.MinValue),
            (_, s) =>
            {
                var fails = s.LockedUntilUtc != DateTime.MinValue && s.LockedUntilUtc <= DateTime.UtcNow ? 1 : s.Failures + 1;
                return fails >= max
                    ? (0, DateTime.UtcNow.AddMinutes(Math.Max(1, _app.LockoutMinutes)))
                    : (fails, DateTime.MinValue);
            });
    }

    public LoginResponse CreateToken(UserRecord user)
    {
        var expires = DateTime.UtcNow.AddHours(Math.Clamp(_jwt.ExpiryHours, 1, 72));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow,
            Expires = expires,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [ClaimNames.UserId] = user.Id,
                [ClaimNames.Name] = user.Name,
                [ClaimNames.Role] = user.Role,
                [ClaimNames.Username] = user.Username,
                [ClaimNames.Stamp] = user.SecurityStamp,
                [ClaimNames.MustChangePassword] = user.MustChangePassword ? "1" : "0"
            }
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new LoginResponse
        {
            Token = token,
            ExpiresAtUtc = expires,
            UserId = user.Id,
            Name = user.Name,
            Role = user.Role,
            MustChangePassword = user.MustChangePassword
        };
    }

    public async Task<LoginResponse> ChangePasswordAsync(string userId, ChangePasswordRequest req)
    {
        ValidatePasswordStrength(req.NewPassword);
        if (req.NewPassword == req.CurrentPassword)
            throw AppException.BadRequest("New password must be different from the current password.");

        return await _store.WriteLockedAsync(async () =>
        {
            var user = await _store.FindAsync<UserRecord>(userId) ?? throw AppException.NotFound("User not found.");
            if (!BCrypt.Net.BCrypt.Verify(req.CurrentPassword ?? "", user.PasswordHash))
                throw AppException.BadRequest("Current password is incorrect.");

            user.PasswordHash = HashPassword(req.NewPassword);
            user.MustChangePassword = false;
            user.SecurityStamp = Ids.New();
            await _store.UpdateAsync(user);
            return CreateToken(user);
        });
    }

    /// <summary>Called for every authenticated request: the user must still exist, be active and unchanged.</summary>
    public async Task<bool> IsTokenStillValidAsync(ClaimsPrincipal principal)
    {
        var id = principal.FindFirst(ClaimNames.UserId)?.Value;
        var role = principal.FindFirst(ClaimNames.Role)?.Value;
        var stamp = principal.FindFirst(ClaimNames.Stamp)?.Value;
        if (string.IsNullOrEmpty(id)) return false;

        var user = await _store.FindAsync<UserRecord>(id);
        return user != null && user.IsActive && user.Role == role && user.SecurityStamp == stamp;
    }

    /// <summary>Creates the first Admin from configuration when the Users tab is empty.</summary>
    public async Task SeedAdminAsync(SeedOptions seed)
    {
        await _store.WriteLockedAsync(async () =>
        {
            var users = await _store.GetAllAsync<UserRecord>();
            if (users.Count > 0) return;

            if (string.IsNullOrWhiteSpace(seed.AdminUsername) || string.IsNullOrWhiteSpace(seed.AdminPassword))
            {
                _logger.LogWarning("No users exist and Seed:AdminUsername / Seed:AdminPassword are not set. Nobody can log in yet.");
                return;
            }

            ValidatePasswordStrength(seed.AdminPassword);
            var admin = new UserRecord
            {
                Id = Ids.New(),
                Name = string.IsNullOrWhiteSpace(seed.AdminName) ? "Administrator" : seed.AdminName.Trim(),
                Username = seed.AdminUsername.Trim().ToLowerInvariant(),
                PasswordHash = HashPassword(seed.AdminPassword),
                Role = Roles.Admin,
                IsActive = true,
                MustChangePassword = true,
                SecurityStamp = Ids.New(),
                CreatedAt = _clock.Now
            };
            await _store.AddAsync(admin);
            _logger.LogInformation("Created first admin user {Username}. They must change the password at first login.", admin.Username);
        });
    }
}
