using System.Diagnostics;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Diagnostics;

/// <summary>
/// Central CRM HTTP instrumentation. Logs every outbound Dynamics Web API call without
/// mutating request bodies, URI encoding, headers, or authentication.
/// </summary>
public sealed class DynamicsCrmDiagnosticsHandler : DelegatingHandler
{
    private readonly PipelineDiagnostics _diagnostics;
    private readonly IOptionsMonitor<AiDiagnosticsOptions> _options;

    public DynamicsCrmDiagnosticsHandler(
        PipelineDiagnostics diagnostics,
        IOptionsMonitor<AiDiagnosticsOptions> options)
    {
        _diagnostics = diagnostics;
        _options = options;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var sw = Stopwatch.StartNew();
        var pathAndQuery = request.RequestUri?.PathAndQuery ?? request.RequestUri?.ToString() ?? "(unknown)";
        var category = _diagnostics.ClassifyCrmRequest(pathAndQuery);
        var categoryLabel = PipelineDiagnostics.CategoryLabel(category);
        var startUtc = DateTimeOffset.UtcNow;
        var fullUrl = request.RequestUri?.ToString() ?? "(unknown)";

        try
        {
            if (category != CrmRequestCategory.BusinessFetchXml &&
                _options.CurrentValue.LogCrmODataRequests)
            {
                var query = request.RequestUri?.Query ?? string.Empty;
                _diagnostics.LogCrmODataRequest(sb =>
                {
                    sb.AppendLine($"Method: {request.Method}");
                    sb.AppendLine($"Category: {categoryLabel}");
                    sb.AppendLine($"Endpoint: {_diagnostics.SanitizeCrmUrl(fullUrl)}");
                    sb.AppendLine($"Path: {request.RequestUri?.AbsolutePath}");
                    sb.AppendLine($"$select: {ExtractODataParam(query, "$select")}");
                    sb.AppendLine($"$filter: {_diagnostics.RedactText(ExtractODataParam(query, "$filter"))}");
                    sb.AppendLine($"$expand: {ExtractODataParam(query, "$expand")}");
                    sb.AppendLine($"Inspected: {InferInspected(pathAndQuery)}");
                    sb.AppendLine($"StartTimeUtc: {startUtc:u}");
                });
            }

            if (_options.CurrentValue.LogCrmHttpRequests)
            {
                _diagnostics.LogCrmHttpRequest(sb =>
                {
                    sb.AppendLine($"Method: {request.Method}");
                    sb.AppendLine($"Category: {categoryLabel}");
                    sb.AppendLine($"BaseUrl: {request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "(n/a)"}");
                    sb.AppendLine($"Path: {request.RequestUri?.AbsolutePath ?? "(n/a)"}");
                    sb.AppendLine($"Query: {SummarizeQuery(request.RequestUri)}");
                    sb.AppendLine($"FinalUrlSanitized: {_diagnostics.SanitizeCrmUrl(fullUrl)}");
                    sb.AppendLine($"StartTimeUtc: {startUtc:u}");
                    sb.AppendLine("AuthHeaders: [redacted]");
                    if (category == CrmRequestCategory.BusinessFetchXml)
                    {
                        sb.AppendLine("FetchXmlParameter: [decoded document logged in FETCHXML section]");
                    }
                });
            }
        }
        catch
        {
            // Diagnostic failures must not affect CRM I/O.
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            sw.Stop();
            SafeLogResponse(categoryLabel, success: false, status: 0, sw, contentLength: null, ex);
            throw;
        }

        sw.Stop();
        SafeLogResponse(
            categoryLabel,
            response.IsSuccessStatusCode,
            (int)response.StatusCode,
            sw,
            response.Content?.Headers.ContentLength,
            ex: null);
        return response;
    }

    private void SafeLogResponse(
        string categoryLabel,
        bool success,
        int status,
        Stopwatch sw,
        long? contentLength,
        Exception? ex)
    {
        try
        {
            _diagnostics.LogCrmHttpResponse(sb =>
            {
                sb.AppendLine($"Category: {categoryLabel}");
                sb.AppendLine(status > 0
                    ? $"Status: {status}"
                    : "Status: (transport failure)");
                sb.AppendLine($"Success: {success}");
                sb.AppendLine($"DurationMs: {DiagnosticsTiming.ElapsedMs(sw)}");
                if (contentLength is long len)
                {
                    sb.AppendLine($"ResponseSizeBytes: {len}");
                }

                if (ex is not null)
                {
                    sb.AppendLine($"ExceptionType: {ex.GetType().Name}");
                    sb.AppendLine($"Message: {_diagnostics.RedactText(ex.Message)}");
                }

                sb.AppendLine(
                    "Note: CRM business result payloads are never logged here.");
            });
        }
        catch
        {
            // ignore
        }
    }

    private static string SummarizeQuery(Uri? uri)
    {
        if (uri is null || string.IsNullOrEmpty(uri.Query))
        {
            return "(none)";
        }

        var q = uri.Query.TrimStart('?');
        if (q.Contains("fetchXml=", StringComparison.OrdinalIgnoreCase))
        {
            return "fetchXml=[logged separately]";
        }

        return q.Length > 300 ? q[..300] + "…" : q;
    }

    private static string ExtractODataParam(string query, string name)
    {
        if (string.IsNullOrEmpty(query))
        {
            return "(none)";
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(kv[0]);
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "(empty)";
        }

        return "(none)";
    }

    private static string InferInspected(string pathAndQuery)
    {
        if (pathAndQuery.Contains("WhoAmI", StringComparison.OrdinalIgnoreCase))
        {
            return "WhoAmI";
        }

        if (pathAndQuery.Contains("ManyToOneRelationships", StringComparison.OrdinalIgnoreCase))
        {
            return "ManyToOneRelationships";
        }

        if (pathAndQuery.Contains("OneToManyRelationships", StringComparison.OrdinalIgnoreCase))
        {
            return "OneToManyRelationships";
        }

        if (pathAndQuery.Contains("ManyToManyRelationships", StringComparison.OrdinalIgnoreCase))
        {
            return "ManyToManyRelationships";
        }

        if (pathAndQuery.Contains("Attributes", StringComparison.OrdinalIgnoreCase))
        {
            return "AttributeDefinitions";
        }

        if (pathAndQuery.Contains("EntityDefinitions", StringComparison.OrdinalIgnoreCase))
        {
            return "EntityDefinitions";
        }

        return "Dynamics Web API";
    }
}
