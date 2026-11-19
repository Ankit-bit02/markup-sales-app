using MarkUp.Shared;

namespace MarkUp.Api;

/// <summary>Stored user row. Never returned to clients (contains the password hash).</summary>
public class UserRecord : IEntity, IActivatable
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Role { get; set; } = Roles.Sales;
    public string DealerId { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }

    /// <summary>Changes whenever the password or role changes; old tokens stop working.</summary>
    public string SecurityStamp { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public UserDto ToDto() => new()
    {
        Id = Id,
        Name = Name,
        Username = Username,
        Phone = Phone,
        Role = Role,
        DealerId = DealerId,
        IsActive = IsActive
    };
}

/// <summary>Error with an HTTP status and a message that is safe to show to the user.</summary>
public class AppException : Exception
{
    public int StatusCode { get; }

    public AppException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public static AppException BadRequest(string message) => new(StatusCodes.Status400BadRequest, message);
    public static AppException NotFound(string message) => new(StatusCodes.Status404NotFound, message);
    public static AppException Conflict(string message) => new(StatusCodes.Status409Conflict, message);
    public static AppException Forbidden(string message) => new(StatusCodes.Status403Forbidden, message);
}

public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N");
}


/// <summary>Personal details of a user, one row per user in the "Profiles" tab. Id = the user's Id.</summary>
public class UserProfileRecord : IEntity
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }
    public string Sex { get; set; } = "";
    public string MaritalStatus { get; set; } = "";
    public string Address { get; set; } = "";
    public string EmergencyPhone { get; set; } = "";
    public DateTime UpdatedAt { get; set; }

    /// <summary>Kept as the last column so the tab stays readable.</summary>
    public string Photo { get; set; } = "";
}