using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace RagBackend.Api.Middleware;

/// <summary>
/// Counts invalid JWT attempts (HTTP 401 responses to requests that carried a Bearer token)
/// and blocks IPs that exceed <see cref="MaxFailures"/> failures within a 60-second window.
///
/// IMPORTANT: This middleware must NOT buffer or replace the response body — doing so would
/// break SSE streaming for the /api/query endpoint. Instead it uses Response.OnStarting to
/// inspect the status code the moment headers are committed, which is non-invasive and works
/// correctly for both streaming and non-streaming responses.
/// </summary>
public class InvalidJwtRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;
    private readonly ILogger<InvalidJwtRateLimitMiddleware> _logger;
    private const int MaxFailures = 10;

    public InvalidJwtRateLimitMiddleware(
        RequestDelegate next,
        IMemoryCache cache,
        ILogger<InvalidJwtRateLimitMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip rate limiting for the login endpoint itself
        if (context.Request.Path.StartsWithSegments("/api/auth"))
        {
            await _next(context);
            return;
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var windowStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var cacheKey = $"ratelimit:{ip}:{windowStart}";

        // Block immediately if the IP has already exceeded the failure threshold
        if (_cache.TryGetValue(cacheKey, out int existingCount) && existingCount > MaxFailures)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Too many requests\"}");
            return;
        }

        // Capture whether this request carries a Bearer token *before* forwarding,
        // so we can reference it safely inside the OnStarting callback.
        var hasBearerToken = context.Request.Headers.Authorization
            .ToString()
            .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

        // Register a callback that fires the instant the response headers are about to be sent.
        // This is non-invasive — it never touches the response body — so SSE streaming is
        // completely unaffected.
        context.Response.OnStarting(() =>
        {
            if (context.Response.StatusCode == StatusCodes.Status401Unauthorized && hasBearerToken)
            {
                var count = _cache.GetOrCreate(cacheKey, entry =>
                {
                    entry.AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(120); // window + buffer
                    return 0;
                });

                count++;
                _cache.Set(cacheKey, count, DateTimeOffset.UtcNow.AddSeconds(120));

                _logger.LogWarning(
                    "Invalid JWT from IP {Ip}: attempt {Count} in window {Window}",
                    ip, count, windowStart);

                if (count > MaxFailures)
                {
                    _logger.LogWarning(
                        "Rate limit exceeded for IP {Ip}: {Count} invalid JWT attempts in 60s window",
                        ip, count);
                }
            }

            return Task.CompletedTask;
        });

        await _next(context);
    }
}
