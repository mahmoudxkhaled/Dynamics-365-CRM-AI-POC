namespace CrmAiPoc.Api.Crm.Query;

/// <summary>
/// Resolves the POC business timezone (default Africa/Cairo) with Windows ID fallback.
/// </summary>
public static class BusinessTimeZone
{
    public const string DefaultIanaId = "Africa/Cairo";
    public const string WindowsFallbackId = "Egypt Standard Time";

    public static TimeZoneInfo Resolve(string? configuredId = null)
    {
        var candidates = new[]
        {
            configuredId,
            DefaultIanaId,
            WindowsFallbackId
        };

        foreach (var id in candidates)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
                // try next
            }
            catch (InvalidTimeZoneException)
            {
                // try next
            }
        }

        return TimeZoneInfo.Utc;
    }
}
