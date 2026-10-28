namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Builds user-facing chat replies when the model returns text without a tool call.
/// CRM row formatting for query_crm is handled by <see cref="GenericCrmReplyFormatter"/>.
/// </summary>
public static class LocalCrmReplyFormatter
{
    public static string FormatRefusalOrText(string? modelText, string languageHint)
    {
        if (!string.IsNullOrWhiteSpace(modelText))
        {
            return modelText.Trim();
        }

        return languageHint.Equals("ar", StringComparison.OrdinalIgnoreCase)
            ? "يمكنني المساعدة فقط في استعلامات CRM للقراءة على الكيانات المعتمدة عبر query_crm."
            : "I can only help with approved read-only CRM queries via query_crm.";
    }
}
