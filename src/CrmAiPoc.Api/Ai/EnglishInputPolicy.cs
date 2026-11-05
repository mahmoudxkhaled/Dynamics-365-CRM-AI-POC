namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Narrow Arabic-script gate for user questions. Does not inspect CRM record values.
/// </summary>
public static class EnglishInputPolicy
{
    public const string RequireEnglishMessage = "Please enter your CRM question in English.";

    /// <summary>
    /// True when the text contains Arabic Unicode letters (U+0600–U+06FF).
    /// </summary>
    public static bool ContainsArabicScript(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (ch is >= '\u0600' and <= '\u06FF')
            {
                return true;
            }
        }

        return false;
    }
}
