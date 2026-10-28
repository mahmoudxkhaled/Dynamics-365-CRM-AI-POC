using System.Net;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Security;

/// <summary>
/// Restricts unauthenticated POC HTTP APIs to loopback callers until proper incoming auth exists.
/// </summary>
public sealed class LoopbackOnlyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CrmOptions _options;
    private readonly ILogger<LoopbackOnlyMiddleware> _logger;

    public LoopbackOnlyMiddleware(
        RequestDelegate next,
        IOptions<CrmOptions> options,
        ILogger<LoopbackOnlyMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.RestrictToLoopback)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        var isApi = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase);
        if (!isApi)
        {
            await _next(context);
            return;
        }

        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote) && !IsMappedLoopback(remote))
        {
            _logger.LogWarning(
                "Rejected non-loopback API request from {RemoteIp} to {Path}. Incoming auth is not implemented for this POC.",
                remote,
                path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "This CRM AI POC accepts local/loopback API callers only until incoming authentication is implemented.",
                remoteIp = remote.ToString()
            });
            return;
        }

        await _next(context);
    }

    private static bool IsMappedLoopback(IPAddress address) =>
        address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4());
}
