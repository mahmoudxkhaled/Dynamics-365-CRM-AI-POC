namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Builds user-facing chat replies when the model returns text without a tool call.
/// Successful query_crm results use structured data only (no generated result summaries).
/// </summary>
public static class LocalCrmReplyFormatter
{
    public static string FormatRefusalOrText(string? modelText, string languageHint)
    {
        _ = languageHint;
        if (!string.IsNullOrWhiteSpace(modelText))
        {
            return modelText.Trim();
        }

        return "I can only help with approved read-only CRM queries via query_crm, clarify_crm, or unsupported_crm_request.";
    }
}
