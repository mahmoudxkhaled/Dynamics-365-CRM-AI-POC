using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Diagnostics;

/// <summary>
/// Console-oriented diagnostic sections for AI / metadata / FetchXML / CRM HTTP.
/// All public methods swallow exceptions so logging never breaks chat execution.
/// </summary>
public sealed class PipelineDiagnostics
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex GuidPattern = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled);

    private static readonly Regex EmailPattern = new(
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PhonePattern = new(
        @"\+?\d[\d\s\-()]{7,}\d",
        RegexOptions.Compiled);

    private readonly IOptionsMonitor<AiDiagnosticsOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<PipelineDiagnostics> _logger;

    public PipelineDiagnostics(
        IOptionsMonitor<AiDiagnosticsOptions> options,
        IHostEnvironment environment,
        ILogger<PipelineDiagnostics> logger)
    {
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    public bool IsEnabled => _options.CurrentValue.Enabled;

    public bool AllowSensitivePayloads =>
        _options.CurrentValue.Enabled &&
        _options.CurrentValue.AllowSensitivePayloadLogging &&
        _environment.IsDevelopment();

    public void LogMetadata(Action<StringBuilder> write) =>
        LogIf(_options.CurrentValue.LogMetadata, "CRM METADATA", write);

    public void LogAiRequest(Action<StringBuilder> write) =>
        LogIf(_options.CurrentValue.LogAiRequest, "AI REQUEST", write);

    public void LogAiResponse(Action<StringBuilder> write) =>
        LogIf(_options.CurrentValue.LogAiResponse, "AI RESPONSE", write);

    public void LogAiError(Action<StringBuilder> write) =>
        LogIf(_options.CurrentValue.LogAiResponse || _options.CurrentValue.LogAiRequest, "AI ERROR", write);

    public void LogQueryPlan(Action<StringBuilder> write) =>
        LogIf(_options.CurrentValue.LogQueryPlan, "QUERY PLAN", write);

    public void LogFetchXml(Action<StringBuilder> write) =>
        LogIf(_options.CurrentValue.LogFetchXml, "FETCHXML", write);

    public void LogCrmHttpRequest(Action<StringBuilder> write) =>
        LogIf(_options.CurrentValue.LogCrmHttpRequests, "CRM HTTP REQUEST", write);

    public void LogCrmHttpResponse(Action<StringBuilder> write) =>
        LogIf(_options.CurrentValue.LogCrmHttpResponses, "CRM HTTP RESPONSE", write);

    public void LogCrmODataRequest(Action<StringBuilder> write) =>
        LogIf(_options.CurrentValue.LogCrmODataRequests, "CRM ODATA REQUEST", write);

    public string PrettyJsonSerialize(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, PrettyJson);
        }
        catch (Exception ex)
        {
            return $"(serialization failed: {ex.GetType().Name})";
        }
    }

    public string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (AllowSensitivePayloads)
        {
            return text;
        }

        var s = GuidPattern.Replace(text, "{guid}");
        s = EmailPattern.Replace(s, "{email}");
        s = PhonePattern.Replace(s, "{phone}");
        return s;
    }

    public string SanitizeUserQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return "(empty)";
        }

        if (AllowSensitivePayloads)
        {
            return question;
        }

        return $"(length={question.Trim().Length}; content redacted — set AiDiagnostics:AllowSensitivePayloadLogging=true in Development)";
    }

    public string FormatFetchXmlForLog(string? fetchXml)
    {
        if (string.IsNullOrWhiteSpace(fetchXml))
        {
            return "(empty)";
        }

        try
        {
            var doc = XDocument.Parse(fetchXml);
            if (!AllowSensitivePayloads)
            {
                foreach (var attr in doc.Descendants().Attributes()
                             .Where(a => a.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase)))
                {
                    if (LooksLikeIsoDate(attr.Value) ||
                        attr.Value is "true" or "false" ||
                        int.TryParse(attr.Value, out _))
                    {
                        continue;
                    }

                    attr.Value = "***";
                }
            }

            return doc.ToString();
        }
        catch
        {
            return AllowSensitivePayloads ? fetchXml : RedactText(fetchXml);
        }
    }

    public string SanitizeCrmUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "(empty)";
        }

        var idx = url.IndexOf("fetchXml=", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var prefix = url[..(idx + "fetchXml=".Length)];
            var rest = url[(idx + "fetchXml=".Length)..];
            var amp = rest.IndexOf('&');
            var sanitized = amp >= 0
                ? prefix + "[logged separately in FETCHXML]" + rest[amp..]
                : prefix + "[logged separately in FETCHXML]";
            return AllowSensitivePayloads ? sanitized : RedactText(sanitized);
        }

        return AllowSensitivePayloads ? url : RedactText(url);
    }

    public string ApplySectionBudget(string body)
    {
        var configured = _options.CurrentValue.MaxSectionChars;
        var max = configured <= 0 ? 100_000 : configured;
        if (body.Length <= max)
        {
            return body;
        }

        return body[..max] +
               $"{Environment.NewLine}... [TRUNCATED: {body.Length - max} chars omitted; raise AiDiagnostics:MaxSectionChars to inspect more]";
    }

    public CrmRequestCategory ClassifyCrmRequest(string? pathOrUrl)
    {
        var path = pathOrUrl ?? string.Empty;
        if (path.Contains("fetchXml=", StringComparison.OrdinalIgnoreCase))
        {
            return CrmRequestCategory.BusinessFetchXml;
        }

        if (path.Contains("WhoAmI", StringComparison.OrdinalIgnoreCase))
        {
            return CrmRequestCategory.WhoAmI;
        }

        if (path.Contains("EntityDefinitions", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Attribute", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Relationship", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("ManyToOne", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("OneToMany", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("ManyToMany", StringComparison.OrdinalIgnoreCase))
        {
            return CrmRequestCategory.MetadataDiscovery;
        }

        return CrmRequestCategory.Other;
    }

    public static string CategoryLabel(CrmRequestCategory category) => category switch
    {
        CrmRequestCategory.BusinessFetchXml => "Business FetchXML query",
        CrmRequestCategory.MetadataDiscovery => "Metadata discovery",
        CrmRequestCategory.RelationshipMetadata => "Relationship metadata",
        CrmRequestCategory.WhoAmI => "WhoAmI",
        _ => "Other Dynamics Web API read"
    };

    private void LogIf(bool sectionEnabled, string section, Action<StringBuilder> write)
    {
        if (!_options.CurrentValue.Enabled || !sectionEnabled)
        {
            return;
        }

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("==================================================");
            sb.AppendLine($"[{section}] CorrelationId: {DiagnosticsCorrelation.CurrentOrNone}");
            sb.AppendLine("==================================================");
            write(sb);
            var text = ApplySectionBudget(sb.ToString().TrimEnd());
            _logger.LogInformation("{DiagnosticSection}", text);
        }
        catch (Exception ex)
        {
            // Never break the chat pipeline because diagnostics failed.
            _logger.LogDebug(ex, "Pipeline diagnostics failed for section {Section}", section);
        }
    }

    private static bool LooksLikeIsoDate(string value) =>
        value.Length >= 10 &&
        char.IsDigit(value[0]) &&
        value.Contains('-', StringComparison.Ordinal) &&
        (value.Contains('T', StringComparison.Ordinal) || value.Length == 10);
}

public enum CrmRequestCategory
{
    BusinessFetchXml,
    MetadataDiscovery,
    RelationshipMetadata,
    WhoAmI,
    Other
}

/// <summary>Stopwatch helper that never throws.</summary>
public static class DiagnosticsTiming
{
    public static long ElapsedMs(Stopwatch sw)
    {
        try
        {
            return sw.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }
}
