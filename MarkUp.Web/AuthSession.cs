using MarkUp.Shared;

namespace MarkUp.Web;

/// <summary>The signed-in user, kept in browser storage so the app survives reloads and works offline.</summary>
public sealed class AuthSession
{
    private const string StorageKey = "mk_auth";
    private readonly Browser _browser;

    public AuthSession(Browser browser) => _browser = browser;

    public LoginResponse? Current { get; private set; }

    public event Action? Changed;

    public bool IsLoggedIn => Current != null && Current.ExpiresAtUtc > DateTime.UtcNow;
    public string Role => IsLoggedIn ? Current!.Role : "";
    public string UserId => IsLoggedIn ? Current!.UserId : "";
    public string Name => IsLoggedIn ? Current!.Name : "";
    public string Token => IsLoggedIn ? Current!.Token : "";
    public bool MustChangePassword => IsLoggedIn && Current!.MustChangePassword;

    /// <summary>Profile picture (data URL) of the signed-in user, shown in the menu. Empty = initials.</summary>
    public string Photo { get; private set; } = "";

    public void SetPhoto(string? photo)
    {
        photo ??= "";
        if (Photo == photo) return;
        Photo = photo;
        Changed?.Invoke();
    }
    public bool IsInRole(params string[] roles) => IsLoggedIn && roles.Contains(Current!.Role);

    public async Task InitializeAsync()
    {
        var saved = await _browser.GetJsonAsync<LoginResponse>(StorageKey);
        if (saved != null && saved.ExpiresAtUtc > DateTime.UtcNow && !string.IsNullOrEmpty(saved.Token))
        {
            Current = saved;
        }
        else if (saved != null)
        {
            await _browser.RemoveItemAsync(StorageKey);
        }
    }

    public async Task SetAsync(LoginResponse response)
    {
        if (Current?.UserId != response.UserId) Photo = "";
        Current = response;
        await _browser.SetJsonAsync(StorageKey, response);
        Changed?.Invoke();
    }

    public async Task ClearAsync()
    {
        Current = null;
        Photo = "";
        await _browser.RemoveItemAsync(StorageKey);
        Changed?.Invoke();
    }

    /// <summary>The page a user lands on after login.</summary>
    public string HomePath => Role switch
    {
        Roles.Sales => "sales",
        Roles.Bill => "billing",
        Roles.Admin => "admin",
        _ => "login"
    };
}
