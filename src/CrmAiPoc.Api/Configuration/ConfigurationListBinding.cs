namespace CrmAiPoc.Api.Configuration;

/// <summary>
/// Helpers for binding configuration string arrays with <b>replace</b> semantics.
/// ASP.NET Core's default key merge keeps higher indices from lower-priority files when a
/// shorter array is defined in appsettings.{Environment}.json — which incorrectly expands
/// allowlists such as <c>Crm:ApprovedEntities</c>.
/// </summary>
public static class ConfigurationListBinding
{
    /// <summary>
    /// Reads <paramref name="key"/> (e.g. <c>Crm:ApprovedEntities</c>) from the highest-priority
    /// provider that defines index 0, then consecutive indices until a gap.
    /// </summary>
    public static List<string> GetStringArrayReplace(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(key))
        {
            return [];
        }

        var normalizedKey = key.Trim().TrimEnd(':');

        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers.Reverse())
            {
                if (!provider.TryGet($"{normalizedKey}:0", out _))
                {
                    continue;
                }

                var list = new List<string>();
                for (var i = 0; ; i++)
                {
                    if (!provider.TryGet($"{normalizedKey}:{i}", out var value))
                    {
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        list.Add(value.Trim());
                    }
                }

                return list;
            }
        }

        // Non-root / single-provider fallback (unit tests with plain sections).
        return configuration.GetSection(normalizedKey).GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();
    }

    /// <summary>
    /// Returns the winning configuration provider type name for diagnostics (never values/secrets).
    /// </summary>
    public static string? DescribeWinningArrayProvider(IConfiguration configuration, string key)
    {
        if (configuration is not IConfigurationRoot root || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var normalizedKey = key.Trim().TrimEnd(':');
        foreach (var provider in root.Providers.Reverse())
        {
            if (provider.TryGet($"{normalizedKey}:0", out _))
            {
                return provider.GetType().Name;
            }
        }

        return null;
    }
}
