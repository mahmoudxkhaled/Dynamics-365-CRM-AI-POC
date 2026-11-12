using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Backend-controlled allowlist for assistant text that may be stored and resent to Groq.
/// Never trusts the model to decide privacy safety.
/// </summary>
public static partial class AssistantHistoryPrivacyFilter
{
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "clarification",
        "unsupported",
        "answer"
    };

    /// <summary>
    /// Returns true when <paramref name="kind"/> and <paramref name="text"/> may be shared with Groq.
    /// </summary>
    public static bool IsAllowed(string? kind, string? text, object? structuredData)
    {
        if (structuredData is not null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(kind) || !AllowedKinds.Contains(kind.Trim()))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length < 1 || trimmed.Length > 2000)
        {
            return false;
        }

        if (ContainsDisallowedPayloadMarkers(trimmed))
        {
            return false;
        }

        var probe = new List<ChatMessage> { new(ChatRole.Assistant, trimmed) };
        var audit = OutboundAiPayloadAuditor.Audit(probe);
        return audit.SafeForLiveCrm && !audit.ContainsFunctionResults;
    }

    public static bool ContainsDisallowedPayloadMarkers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (text.Contains("<fetch", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("</fetch>", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("link-entity", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("\"records\"", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("FunctionCall", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("FunctionResult", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("discover_crm_schema", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("query_crm", StringComparison.OrdinalIgnoreCase) ||
            FetchXmlTagPattern().IsMatch(text))
        {
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"<\s*fetch\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FetchXmlTagPattern();
}
