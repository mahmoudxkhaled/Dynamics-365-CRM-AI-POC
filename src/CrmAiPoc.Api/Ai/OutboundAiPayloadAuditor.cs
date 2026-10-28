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
        var hasFunctionResults = list.Any(m =>
            m.Contents.Any(c => c is FunctionResultContent));

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
            SafeForLiveCrm = !hasFunctionResults && !suspicious
        };
    }

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
