using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarkUp.Shared;
using Microsoft.AspNetCore.Components;

namespace MarkUp.Web;

public sealed class ApiException : Exception
{
    public int StatusCode { get; }

    /// <summary>True when the server could not be reached at all (offline, DNS, server asleep and timed out).</summary>
    public bool IsNetworkError { get; }

    public ApiException(string message, int statusCode, bool isNetworkError = false) : base(message)
    {
        StatusCode = statusCode;
        IsNetworkError = isNetworkError;
    }

    /// <summary>Server-side or availability problems that are worth retrying later.</summary>
    public bool IsTransient => IsNetworkError || StatusCode >= 500 || StatusCode == 429 || StatusCode == 408;
}

public sealed class ApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly AuthSession _auth;
    private readonly NavigationManager _nav;

    public ApiClient(HttpClient http, AuthSession auth, NavigationManager nav)
    {
        _http = http;
        _auth = auth;
        _nav = nav;
    }

    public Task<T> GetAsync<T>(string url) => SendAsync<T>(HttpMethod.Get, url, null);

    public Task<T> PostAsync<T>(string url, object? body) => SendAsync<T>(HttpMethod.Post, url, body);

    public Task<T> PutAsync<T>(string url, object? body) => SendAsync<T>(HttpMethod.Put, url, body);

    public async Task DeleteAsync(string url)
    {
        using var resp = await RawAsync(HttpMethod.Delete, url, null);
    }

    public async Task<(byte[] Data, string FileName, string ContentType)> DownloadAsync(string url, string fallbackName)
    {
        using var resp = await RawAsync(HttpMethod.Get, url, null);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var name = resp.Content.Headers.ContentDisposition?.FileNameStar
                   ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                   ?? fallbackName;
        var type = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return (bytes, name, type);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body)
    {
        using var resp = await RawAsync(method, url, body);
        try
        {
            var result = await resp.Content.ReadFromJsonAsync<T>(Json);
            return result ?? throw new ApiException("The server returned an empty response.", (int)resp.StatusCode);
        }
        catch (JsonException)
        {
            throw new ApiException("The server returned an unexpected response.", (int)resp.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> RawAsync(HttpMethod method, string url, object? body)
    {
        using var req = new HttpRequestMessage(method, url);
        if (_auth.IsLoggedIn)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.Token);
        }
        if (body != null)
        {
            req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req);
        }
        catch (HttpRequestException)
        {
            throw new ApiException("Cannot reach the server. Check your internet connection.", 0, isNetworkError: true);
        }
        catch (TaskCanceledException)
        {
            throw new ApiException("The server took too long to respond. It may be starting up - try again in a minute.", 0, isNetworkError: true);
        }

        if (resp.IsSuccessStatusCode) return resp;

        var status = (int)resp.StatusCode;
        var message = await ReadErrorAsync(resp);
        resp.Dispose();

        if (status == (int)HttpStatusCode.Unauthorized && !url.StartsWith("api/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            await _auth.ClearAsync();
            _nav.NavigateTo("login");
            throw new ApiException("Your session has ended. Please log in again.", status);
        }

        if (status == (int)HttpStatusCode.Forbidden && message.Contains("change your password", StringComparison.OrdinalIgnoreCase))
        {
            _nav.NavigateTo("change-password");
        }

        throw new ApiException(message, status);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ApiError>(Json);
            if (!string.IsNullOrWhiteSpace(err?.Message)) return err!.Message;
        }
        catch
        {
            // not JSON
        }

        return resp.StatusCode switch
        {
            HttpStatusCode.Forbidden => "You do not have access to this.",
            HttpStatusCode.NotFound => "Not found.",
            HttpStatusCode.TooManyRequests => "Too many requests. Please wait a moment.",
            _ => $"Request failed ({(int)resp.StatusCode})."
        };
    }
}
