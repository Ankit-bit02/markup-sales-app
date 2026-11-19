using System.Text.RegularExpressions;
using MarkUp.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarkUp.Api;

/// <summary>
/// Personal profile. Every user (Admin, Sales, Bill) edits only their own profile.
/// Admin can view (not edit) any user's profile.
/// </summary>
[ApiController]
[Route("api/profile")]
[Authorize]
public sealed class ProfileController : ControllerBase
{
    private static readonly Regex PhotoPattern =
        new(@"^data:image/(jpeg|png|webp);base64,([A-Za-z0-9+/]+={0,2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SheetStore _store;
    private readonly AppClock _clock;

    public ProfileController(SheetStore store, AppClock clock)
    {
        _store = store;
        _clock = clock;
    }

    [HttpGet("me")]
    public async Task<ActionResult<ProfileView>> GetMine() =>
        Ok(await BuildAsync(User.UserId()));

    [HttpGet("{userId}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<ProfileView>> GetForUser(string userId) =>
        Ok(await BuildAsync(userId));

    [HttpPut("me")]
    public async Task<ActionResult<ProfileView>> SaveMine([FromBody] ProfileSaveRequest req)
    {
        Normalize(req);
        Validate(req);
        var userId = User.UserId();

        await _store.WriteLockedAsync(async () =>
        {
            var user = await _store.FindAsync<UserRecord>(userId) ?? throw AppException.NotFound("User not found.");
            if (user.Phone != req.Phone)
            {
                user.Phone = req.Phone;
                await _store.UpdateAsync(user);
            }

            var profile = await _store.FindAsync<UserProfileRecord>(userId);
            var isNew = profile == null;
            profile ??= new UserProfileRecord { Id = userId };

            profile.Email = req.Email;
            profile.DateOfBirth = req.DateOfBirth;
            profile.Sex = req.Sex;
            profile.MaritalStatus = req.MaritalStatus;
            profile.Address = req.Address;
            profile.EmergencyPhone = req.EmergencyPhone;
            profile.Photo = req.Photo;
            profile.UpdatedAt = _clock.Now;

            if (isNew) await _store.AddAsync(profile);
            else await _store.UpdateAsync(profile);
        });

        return Ok(await BuildAsync(userId));
    }

    private async Task<ProfileView> BuildAsync(string userId)
    {
        var user = await _store.FindAsync<UserRecord>(userId) ?? throw AppException.NotFound("User not found.");
        var profile = await _store.FindAsync<UserProfileRecord>(userId) ?? new UserProfileRecord { Id = userId };
        var dealer = await _store.FindAsync<Dealer>(user.DealerId);

        return new ProfileView
        {
            UserId = user.Id,
            Name = user.Name,
            Username = user.Username,
            Role = user.Role,
            DealerName = dealer?.Name ?? "",
            Phone = user.Phone,
            Email = profile.Email,
            DateOfBirth = profile.DateOfBirth,
            Age = AgeOn(profile.DateOfBirth, _clock.Today),
            Sex = profile.Sex,
            MaritalStatus = profile.MaritalStatus,
            Address = profile.Address,
            EmergencyPhone = profile.EmergencyPhone,
            Photo = profile.Photo,
            UpdatedAt = profile.UpdatedAt == default ? "" : AppClock.StampText(profile.UpdatedAt)
        };
    }

    private static int? AgeOn(DateOnly? dob, DateOnly today)
    {
        if (dob is not { } d) return null;
        var age = today.Year - d.Year;
        if (today < d.AddYears(age)) age--;
        return age;
    }

    private static void Normalize(ProfileSaveRequest req)
    {
        req.Phone = (req.Phone ?? "").Trim();
        req.Email = (req.Email ?? "").Trim().ToLowerInvariant();
        req.Sex = (req.Sex ?? "").Trim();
        req.MaritalStatus = (req.MaritalStatus ?? "").Trim();
        req.Address = (req.Address ?? "").Trim();
        req.EmergencyPhone = (req.EmergencyPhone ?? "").Trim();
        req.Photo = (req.Photo ?? "").Trim();
    }

    private void Validate(ProfileSaveRequest req)
    {
        if (req.Sex.Length > 0 && !ProfileOptions.Sexes.Contains(req.Sex))
            throw AppException.BadRequest("Select a valid sex.");

        if (req.MaritalStatus.Length > 0 && !ProfileOptions.MaritalStatuses.Contains(req.MaritalStatus))
            throw AppException.BadRequest("Select a valid marital status.");

        if (req.DateOfBirth is { } dob)
        {
            var age = AgeOn(dob, _clock.Today) ?? 0;
            if (dob > _clock.Today || age < 14 || age > 100)
                throw AppException.BadRequest("Enter a valid date of birth.");
        }

        if (req.Photo.Length > 0)
        {
            if (req.Photo.Length > ProfileOptions.MaxPhotoChars)
                throw AppException.BadRequest("The profile picture is too large. Choose a smaller picture.");

            var match = PhotoPattern.Match(req.Photo);
            if (!match.Success)
                throw AppException.BadRequest("The profile picture must be a JPG, PNG or WebP image.");

            var bytes = new byte[match.Groups[2].Value.Length];
            if (!Convert.TryFromBase64String(match.Groups[2].Value, bytes, out var written) || !LooksLikeImage(bytes.AsSpan(0, written)))
                throw AppException.BadRequest("The profile picture could not be read. Choose another picture.");
        }
    }

    /// <summary>Checks the file signature so only real images are stored.</summary>
    private static bool LooksLikeImage(ReadOnlySpan<byte> b) =>
        (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ||                              // JPEG
        (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||              // PNG
        (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
                       && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P');                  // WebP
}