using System.Text;
using Microsoft.Extensions.AI;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Audits outbound AI messages to ensure live CRM records / tool results are not included.
/// </summary>
public static class OutboundAiPayloadAuditor
{
    public static OutboundAuditResult Audit(IEnumerable<ChatMessage> messages)
    {
        var list = messages.ToList();
        var hasFunctionResults = list.Any(m =>
            m.Contents.Any(c => c is FunctionResultContent));

        var roles = string.Join(",", list.Select(m => m.Role.Value));
        var contentTypes = string.Join(",",
            list.SelectMany(m => m.Contents.Select(c => c.GetType().Name)).Distinct());

        // Rough scan for sales order GUIDs / order numbers patterns in text going out.
        var textBlob = new StringBuilder();
        foreach (var message in list)
        {
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                textBlob.Append(message.Text);
            }
        }

        var text = textBlob.ToString();
        var suspicious =
            text.Contains("ORD-", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("salesorderid", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("_te_customeraccountid_value", StringComparison.OrdinalIgnoreCase);

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
