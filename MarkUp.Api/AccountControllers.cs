using MarkUp.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MarkUp.Api;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    private readonly SheetStore _store;

    public AuthController(AuthService auth, SheetStore store)
    {
        _auth = auth;
        _store = store;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest req) =>
        Ok(await _auth.LoginAsync(req));

    [HttpPost("change-password")]
    [Authorize]
    public async Task<ActionResult<LoginResponse>> ChangePassword([FromBody] ChangePasswordRequest req) =>
        Ok(await _auth.ChangePasswordAsync(User.UserId(), req));

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> Me()
    {
        var user = await _store.FindAsync<UserRecord>(User.UserId()) ?? throw AppException.NotFound("User not found.");
        return Ok(user.ToDto());
    }
}

[ApiController]
[Route("api/users")]
[Authorize(Roles = Roles.Admin)]
public sealed class UsersController : ControllerBase
{
    private readonly SheetStore _store;
    private readonly AppClock _clock;

    public UsersController(SheetStore store, AppClock clock)
    {
        _store = store;
        _clock = clock;
    }

    [HttpGet]
    public async Task<ActionResult<List<UserDto>>> GetAll()
    {
        var users = await _store.GetAllAsync<UserRecord>();
        return Ok(users.OrderBy(u => u.Name).Select(u => u.ToDto()).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> Create([FromBody] UserSaveRequest req)
    {
        Normalize(req);
        AuthService.ValidatePasswordStrength(req.Password);

        var result = await _store.WriteLockedAsync(async () =>
        {
            var users = await _store.GetAllAsync<UserRecord>();
            await ValidateAsync(req, users, null);

            var user = new UserRecord
            {
                Id = Ids.New(),
                Name = req.Name,
                Username = req.Username,
                Phone = req.Phone,
                Role = req.Role,
                DealerId = req.Role == Roles.Sales ? req.DealerId : "",
                IsActive = req.IsActive,
                PasswordHash = AuthService.HashPassword(req.Password),
                MustChangePassword = true,
                SecurityStamp = Ids.New(),
                CreatedAt = _clock.Now
            };
            await _store.AddAsync(user);
            return user.ToDto();
        });
        return Ok(result);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<UserDto>> Update(string id, [FromBody] UserSaveRequest req)
    {
        Normalize(req);
        if (!string.IsNullOrEmpty(req.Password)) AuthService.ValidatePasswordStrength(req.Password);
        var currentUserId = User.UserId();

        var result = await _store.WriteLockedAsync(async () =>
        {
            var users = await _store.GetAllAsync<UserRecord>();
            var user = users.FirstOrDefault(u => u.Id == id) ?? throw AppException.NotFound("User not found.");
            await ValidateAsync(req, users, user);

            if (id == currentUserId && (!req.IsActive || req.Role != Roles.Admin))
                throw AppException.BadRequest("You cannot deactivate yourself or remove your own Admin role.");

            var stillAdmin = req.IsActive && req.Role == Roles.Admin;
            if (user.Role == Roles.Admin && user.IsActive && !stillAdmin
                && users.Count(u => u.Role == Roles.Admin && u.IsActive) <= 1)
                throw AppException.BadRequest("At least one active Admin must remain.");

            var roleChanged = user.Role != req.Role || user.IsActive != req.IsActive;
            user.Name = req.Name;
            user.Username = req.Username;
            user.Phone = req.Phone;
            user.Role = req.Role;
            user.DealerId = req.Role == Roles.Sales ? req.DealerId : "";
            user.IsActive = req.IsActive;

            if (!string.IsNullOrEmpty(req.Password))
            {
                user.PasswordHash = AuthService.HashPassword(req.Password);
                user.MustChangePassword = true;
                roleChanged = true;
            }
            if (roleChanged) user.SecurityStamp = Ids.New(); // signs the user out everywhere

            await _store.UpdateAsync(user);
            return user.ToDto();
        });
        return Ok(result);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Deactivate(string id)
    {
        if (id == User.UserId()) throw AppException.BadRequest("You cannot deactivate yourself.");

        await _store.WriteLockedAsync(async () =>
        {
            var users = await _store.GetAllAsync<UserRecord>();
            var user = users.FirstOrDefault(u => u.Id == id) ?? throw AppException.NotFound("User not found.");
            if (user.Role == Roles.Admin && user.IsActive && users.Count(u => u.Role == Roles.Admin && u.IsActive) <= 1)
                throw AppException.BadRequest("At least one active Admin must remain.");

            user.IsActive = false;
            user.SecurityStamp = Ids.New();
            await _store.UpdateAsync(user);
        });
        return NoContent();
    }

    private static void Normalize(UserSaveRequest req)
    {
        req.Name = (req.Name ?? "").Trim();
        req.Username = (req.Username ?? "").Trim().ToLowerInvariant();
        req.Phone = (req.Phone ?? "").Trim();
        req.DealerId = (req.DealerId ?? "").Trim();
        req.Password ??= "";
    }

    private async Task ValidateAsync(UserSaveRequest req, List<UserRecord> users, UserRecord? existing)
    {
        if (!Roles.IsValid(req.Role)) throw AppException.BadRequest("Select a valid role.");

        if (users.Any(u => u.Id != existing?.Id && u.Username.Equals(req.Username, StringComparison.OrdinalIgnoreCase)))
            throw AppException.Conflict($"Username \"{req.Username}\" is already taken.");

        if (req.Role == Roles.Sales)
        {
            var dealer = await _store.FindAsync<Dealer>(req.DealerId);
            if (dealer == null || !dealer.IsActive)
                throw AppException.BadRequest("A Sales user must belong to an active dealer.");
        }
    }
}

[ApiController]
[Route("api/lookups")]
[Authorize]
public sealed class LookupsController : ControllerBase
{
    private readonly MasterData _masters;

    public LookupsController(MasterData masters) => _masters = masters;

    /// <summary>Filter lists for reports. Sales users only get themselves and their own beats.</summary>
    [HttpGet]
    public async Task<ActionResult<LookupsResponse>> Get()
    {
        var m = await _masters.LoadAsync();
        var userId = User.UserId();
        var isSales = User.Role() == Roles.Sales;

        var salesUsers = m.Users.Values.Where(u => u.Role == Roles.Sales && (isSales ? u.Id == userId : u.IsActive));
        var beats = m.Beats.Values.Where(b => b.IsActive && (!isSales || b.AssignedUserId == userId));
        var dealerIds = isSales
            ? m.Users.TryGetValue(userId, out var me) ? new HashSet<string> { me.DealerId } : new HashSet<string>()
            : null;
        var dealers = m.Dealers.Values.Where(d => d.IsActive && (dealerIds == null || dealerIds.Contains(d.Id)));

        return Ok(new LookupsResponse
        {
            SalesUsers = salesUsers.OrderBy(u => u.Name).Select(u => new LookupItem { Id = u.Id, Name = u.Name }).ToList(),
            BillUsers = User.Role() == Roles.Admin
                ? m.Users.Values.Where(u => u.Role == Roles.Bill && u.IsActive).OrderBy(u => u.Name)
                    .Select(u => new LookupItem { Id = u.Id, Name = u.Name }).ToList()
                : new List<LookupItem>(),
            Dealers = dealers.OrderBy(d => d.Name).Select(d => new LookupItem { Id = d.Id, Name = d.Name }).ToList(),
            Beats = beats.OrderBy(b => b.Name).Select(b => new LookupItem { Id = b.Id, Name = b.Name }).ToList(),
            Products = m.Products.Values.Where(p => p.IsActive).OrderBy(p => p.Name)
                .Select(p => new LookupItem { Id = p.Id, Name = $"{p.Code} - {p.Name}" }).ToList()
        });
    }
}
