using System.ClientModel;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace CrmAiPoc.Api.Ai;

/// <summary>Classifies AI provider exceptions for timeout/retry diagnostics.</summary>
public static partial class AiProviderFailureClassifier
{
    public static AiProviderFailureKind Classify(Exception ex)
    {
        if (ex is TimeoutException)
        {
            return AiProviderFailureKind.ClientTimeout;
        }

        if (ex is OperationCanceledException)
        {
            // Caller distinguishes user cancellation vs client CancelAfter timeout.
            return AiProviderFailureKind.ClientTimeout;
        }

        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return AiProviderFailureKind.Http429;
            }

            if (httpEx.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return AiProviderFailureKind.Http503;
            }

            if (httpEx.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
            {
                return AiProviderFailureKind.Http400;
            }

            return AiProviderFailureKind.Network;
        }

        if (ex is ClientResultException clientEx)
        {
            var status = (int)clientEx.Status;
            if (status == 429)
            {
                return AiProviderFailureKind.Http429;
            }

            if (status == 503)
            {
                return AiProviderFailureKind.Http503;
            }

            if (status is >= 400 and < 500)
            {
                return AiProviderFailureKind.Http400;
            }

            if (status >= 500)
            {
                return AiProviderFailureKind.Network;
            }
        }

        var message = ex.Message ?? string.Empty;
        if (IsProviderLimitMessage(message))
        {
            return AiProviderFailureKind.ProviderLimit;
        }

        if (ContainsAny(message, "429", "rate limit", "high demand", "too many requests"))
        {
            return AiProviderFailureKind.Http429;
        }

        if (ContainsAny(message, "503", "temporarily unavailable", "service unavailable"))
        {
            return AiProviderFailureKind.Http503;
        }

        if (ContainsAny(message, "400", "bad request", "invalid_request", "tool", "function call", "schema"))
        {
            // Only treat as Http400 when the message looks like a request/tool rejection, not a generic error.
            if (ContainsAny(message, "400", "bad request", "invalid_request") ||
                ContainsAny(message, "tool_use", "tool call", "function_call", "json_validate"))
            {
                return AiProviderFailureKind.Http400;
            }
        }

        if (ContainsAny(message, "timeout", "timed out"))
        {
            return AiProviderFailureKind.ClientTimeout;
        }

        if (ex is IOException || ContainsAny(message, "connection", "network", "socket", "dns", "refused"))
        {
            return AiProviderFailureKind.Network;
        }

        return AiProviderFailureKind.Other;
    }

    public static bool IsTransient(AiProviderFailureKind kind) =>
        kind is AiProviderFailureKind.Http429 or AiProviderFailureKind.Http503;

    public static bool IsProviderLimitMessage(string message) =>
        ContainsAny(
            message,
            "context length",
            "context_length",
            "context_window",
            "maximum context",
            "too many tokens",
            "token limit",
            "request too large",
            "payload too large",
            "413",
            "max_tokens",
            "reduce the length");

    /// <summary>
    /// Bounded delay for transient retries. Honors Retry-After seconds when present in the message.
    /// Cap keeps interactive waits short (max 8s).
    /// </summary>
    public static TimeSpan GetTransientRetryDelay(Exception ex, int attempt)
    {
        var fromHeader = TryParseRetryAfterSeconds(ex.Message);
        if (fromHeader is > 0)
        {
            return TimeSpan.FromSeconds(Math.Clamp(fromHeader.Value, 1, 8));
        }

        var seconds = Math.Clamp(attempt * 2, 2, 8);
        return TimeSpan.FromSeconds(seconds);
    }

    private static int? TryParseRetryAfterSeconds(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var match = RetryAfterRegex().Match(message);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }

    private static bool ContainsAny(string message, params string[] needles) =>
        needles.Any(n => message.Contains(n, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"retry[- ]after[=:\s]+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetryAfterRegex();
}
