using System.Text;
using System.Text.RegularExpressions;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.AI;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Audits outbound AI messages to ensure live CRM records / tool results are not included.
/// Approved metadata catalog blocks (schema only) are allowed when properly delimited.
/// </summary>
public static class OutboundAiPayloadAuditor
{
    private static readonly Regex OrderNumberPattern = new(
        @"\bORD-\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GuidPattern = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled);

    public static OutboundAuditResult Audit(IEnumerable<ChatMessage> messages)
    {
        var list = messages.ToList();
        var functionResults = list
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .ToList();
        var hasFunctionResults = functionResults.Count > 0;
        var hasNonValidationFunctionResults = functionResults.Any(fr =>
            !IsQueryPlanValidationFeedback(fr.Result) &&
            !IsSchemaDiscoveryResult(fr.Result));

        var roles = string.Join(",", list.Select(m => m.Role.Value));
        var contentTypes = string.Join(",",
            list.SelectMany(m => m.Contents.Select(c => c.GetType().Name)).Distinct());

        var textBlob = new StringBuilder();
        foreach (var message in list)
        {
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            // Schema catalog is reference metadata only — exclude from record-pattern scans.
            if (IsApprovedMetadataCatalogBlock(message.Text))
            {
                continue;
            }

            textBlob.Append(message.Text);
        }

        // Include non-validation / non-schema-discovery tool-result payloads in the record-pattern scan.
        foreach (var fr in functionResults.Where(fr =>
                     !IsQueryPlanValidationFeedback(fr.Result) &&
                     !IsSchemaDiscoveryResult(fr.Result)))
        {
            textBlob.Append(Convert.ToString(fr.Result) ?? string.Empty);
        }

        var text = textBlob.ToString();
        // Record-like payloads (order numbers, amount+customer fields) — not schema catalogs.
        var suspicious =
            OrderNumberPattern.IsMatch(text) ||
            text.Contains("ORD-", StringComparison.OrdinalIgnoreCase) ||
            (text.Contains("totalAmount", StringComparison.OrdinalIgnoreCase) &&
             text.Contains("customerAccountId", StringComparison.OrdinalIgnoreCase)) ||
            (GuidPattern.IsMatch(text) &&
             (text.Contains("customerAccountId", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("salesOrders", StringComparison.OrdinalIgnoreCase)));

        return new OutboundAuditResult
        {
            MessageCount = list.Count,
            Roles = roles,
            ContentTypes = contentTypes,
            ContainsFunctionResults = hasFunctionResults,
            SuspiciousCrmRecordText = suspicious,
            // Allow request-local validation feedback and schema-discovery results (no CRM records).
            SafeForLiveCrm = !hasNonValidationFunctionResults && !suspicious
        };
    }

    /// <summary>
    /// True when a tool result is compact query-plan validation feedback (not CRM data).
    /// </summary>
    public static bool IsQueryPlanValidationFeedback(object? result)
    {
        var text = AsText(result);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Must look like our sanitized feedback payload and must not contain record arrays.
        if (text.Contains("salesOrders", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("\"records\"", StringComparison.OrdinalIgnoreCase) ||
            OrderNumberPattern.IsMatch(text))
        {
            return false;
        }

        return text.Contains("\"success\"", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("\"error\"", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("\"instruction\"", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a tool result is compact schema discovery metadata (not CRM business records).
    /// </summary>
    public static bool IsSchemaDiscoveryResult(object? result)
    {
        var text = AsText(result);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains("salesOrders", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("\"records\"", StringComparison.OrdinalIgnoreCase) ||
            OrderNumberPattern.IsMatch(text))
        {
            return false;
        }

        return text.Contains("\"fields\"", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("\"relationships\"", StringComparison.OrdinalIgnoreCase) &&
               (text.Contains("\"candidates\"", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("\"unresolvedConcepts\"", StringComparison.OrdinalIgnoreCase));
    }

    private static string? AsText(object? result) =>
        result switch
        {
            null => null,
            string s => s,
            _ => Convert.ToString(result)
        };

    public static bool IsApprovedMetadataCatalogBlock(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.Contains(CrmAiMetadataCatalogService.BeginMarker, StringComparison.Ordinal) &&
        text.Contains(CrmAiMetadataCatalogService.EndMarker, StringComparison.Ordinal);
}

public sealed class OutboundAuditResult
{
    public int MessageCount { get; init; }
    public string Roles { get; init; } = string.Empty;
    public string ContentTypes { get; init; } = string.Empty;
    public bool ContainsFunctionResults { get; init; }
    public bool SuspiciousCrmRecordText { get; init; }
    public bool SafeForLiveCrm { get; init; }
}
