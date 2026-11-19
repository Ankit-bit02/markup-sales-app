using Google;
using MarkUp.Shared;

namespace MarkUp.Api;

/// <summary>Turns exceptions into JSON { message } responses without leaking internals.</summary>
public sealed class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppException ex)
        {
            await Write(context, ex.StatusCode, ex.Message);
        }
        catch (GoogleApiException ex)
        {
            _logger.LogError(ex, "Google Sheets API error.");
            await Write(context, StatusCodes.Status503ServiceUnavailable,
                "The data store (Google Sheets) is not reachable right now. Please try again shortly.");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to send.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error on {Method} {Path}.", context.Request.Method, context.Request.Path);
            await Write(context, StatusCodes.Status500InternalServerError, "Something went wrong. Please try again.");
        }
    }

    private static async Task Write(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new ApiError { Message = message });
    }
}
