using System.Text.Json;
using Microsoft.JSInterop;

namespace MarkUp.Web;

public sealed class GeoResult
{
    public bool Ok { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double Accuracy { get; set; }
    public string? Error { get; set; }
}

/// <summary>Small wrapper over the browser features the app needs (see wwwroot/js/app.js).</summary>
public sealed class Browser
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IJSRuntime _js;

    public Browser(IJSRuntime js) => _js = js;

    public ValueTask<string?> GetItemAsync(string key) => _js.InvokeAsync<string?>("markup.getItem", key);

    public ValueTask<bool> SetItemAsync(string key, string value) => _js.InvokeAsync<bool>("markup.setItem", key, value);

    public ValueTask RemoveItemAsync(string key) => _js.InvokeVoidAsync("markup.removeItem", key);

    public async Task<T?> GetJsonAsync<T>(string key)
    {
        var text = await GetItemAsync(key);
        if (string.IsNullOrEmpty(text)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(text, Json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public async Task<bool> SetJsonAsync<T>(string key, T value) =>
        await SetItemAsync(key, JsonSerializer.Serialize(value, Json));

    public ValueTask<bool> IsOnlineAsync() => _js.InvokeAsync<bool>("markup.isOnline");

    public ValueTask<GeoResult> GetLocationAsync() => _js.InvokeAsync<GeoResult>("markup.getLocation");

    public ValueTask OpenUrlAsync(string url) => _js.InvokeVoidAsync("markup.openUrl", url);

    public ValueTask DownloadAsync(string fileName, string contentType, byte[] data) =>
        _js.InvokeVoidAsync("markup.download", fileName, contentType, data);

    public ValueTask<bool> ConfirmAsync(string message) => _js.InvokeAsync<bool>("markup.confirm", message);

    public ValueTask RegisterOnlineAsync<T>(DotNetObjectReference<T> target) where T : class =>
        _js.InvokeVoidAsync("markup.onOnline", target);
}
