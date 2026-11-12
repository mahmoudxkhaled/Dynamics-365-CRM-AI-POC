namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Arabic-script detection helper for diagnostics. User questions may be Arabic/English/mixed.
/// Does not inspect CRM record values.
/// </summary>
public static class EnglishInputPolicy
{
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
