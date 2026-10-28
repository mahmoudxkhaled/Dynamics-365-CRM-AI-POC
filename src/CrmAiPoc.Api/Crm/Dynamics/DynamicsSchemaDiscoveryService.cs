using System.Text;
using System.Text.Json;
using CrmAiPoc.Api.Security;

namespace CrmAiPoc.Api.Crm.Dynamics;

/// <summary>
/// Discovers salesorder schema from CRM metadata. Does not guess field names.
/// </summary>
public sealed class DynamicsSchemaDiscoveryService
{
    private readonly DynamicsCrmHttp _http;
    private readonly ILogger<DynamicsSchemaDiscoveryService> _logger;

    public DynamicsSchemaDiscoveryService(
        DynamicsCrmHttp http,
        ILogger<DynamicsSchemaDiscoveryService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<SalesOrderSchemaDiscoveryResult> DiscoverSalesOrderSchemaAsync(
        CancellationToken cancellationToken = default)
    {
        var entityDefPath =
            "EntityDefinitions(LogicalName='salesorder')?$select=LogicalName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute";

        var entityResult = await _http.GetAsync(entityDefPath, cancellationToken);
        if (!entityResult.IsSuccess)
        {
            return Fail($"EntityDefinitions failed: HTTP {entityResult.StatusCode} ({entityResult.ErrorCategory}).");
        }

        using var entityDoc = JsonDocument.Parse(entityResult.Body);
        var entity = entityDoc.RootElement;

        var logicalName = GetString(entity, "LogicalName") ?? "salesorder";
        var entitySetName = GetString(entity, "EntitySetName");
        var primaryId = GetString(entity, "PrimaryIdAttribute");
        var primaryName = GetString(entity, "PrimaryNameAttribute");

        if (string.IsNullOrWhiteSpace(entitySetName) ||
            string.IsNullOrWhiteSpace(primaryId) ||
            string.IsNullOrWhiteSpace(primaryName))
        {
            return Fail("EntityDefinitions response missing EntitySetName or primary attributes.");
        }

        // Attributes (including custom). Keep response size manageable.
        var attributesPath =
            "EntityDefinitions(LogicalName='salesorder')/Attributes" +
            "?$select=LogicalName,AttributeType,SchemaName,DisplayName,IsValidForRead,IsCustomAttribute" +
            "&$filter=IsValidForRead eq true";

        var attributesResult = await _http.GetAsync(attributesPath, cancellationToken);
        if (!attributesResult.IsSuccess)
        {
            return Fail(
                $"Attributes metadata failed: HTTP {attributesResult.StatusCode} ({attributesResult.ErrorCategory}).");
        }

        var attributes = ParseAttributes(attributesResult.Body);
        var custom = attributes.Where(a => a.IsCustomAttribute).ToList();
        var lookups = attributes
            .Where(a => a.AttributeType.Contains("Lookup", StringComparison.OrdinalIgnoreCase) ||
                        a.AttributeType.Contains("Customer", StringComparison.OrdinalIgnoreCase) ||
                        a.AttributeType.Contains("Owner", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Candidate readable fields for allowlist: primary + known common + discovered custom of interest.
        var recommended = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            primaryId,
            primaryName
        };

        foreach (var candidate in new[]
                 {
                     "ordernumber",
                     "createdon",
                     "statuscode",
                     "statecode",
                     "customerid",
                     "te_customeraccountid",
                     "_te_customeraccountid_value",
                     "_customerid_value"
                 })
        {
            if (attributes.Any(a => a.LogicalName.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                                    a.LogicalName.Equals(candidate.TrimStart('_').Replace("_value", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase)))
            {
                // Prefer OData bind value names as returned by Web API for lookups.
                if (candidate.StartsWith('_') || attributes.Any(a => a.LogicalName.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    recommended.Add(candidate.StartsWith('_') ? candidate : ResolveSelectName(candidate, attributes));
                }
            }
        }

        // Always include te_customeraccountid if present as attribute logical name.
        if (attributes.Any(a => a.LogicalName.Equals("te_customeraccountid", StringComparison.OrdinalIgnoreCase)))
        {
            recommended.Add("_te_customeraccountid_value");
        }

        var report = BuildReport(logicalName, entitySetName, primaryId, primaryName, attributes, custom, lookups, recommended);

        _logger.LogInformation(
            "Discovered salesorder schema: EntitySetName={EntitySetName}, PrimaryId={PrimaryId}, PrimaryName={PrimaryName}, AttributeCount={Count}",
            entitySetName,
            primaryId,
            primaryName,
            attributes.Count);

        return new SalesOrderSchemaDiscoveryResult
        {
            Success = true,
            LogicalName = logicalName,
            EntitySetName = entitySetName,
            PrimaryIdAttribute = primaryId,
            PrimaryNameAttribute = primaryName,
            Attributes = attributes,
            CustomAttributes = custom,
            LookupAttributes = lookups,
            RecommendedSelectFields = recommended.ToArray(),
            MarkdownReport = report,
            Message = "Schema discovery succeeded."
        };
    }

    private static string ResolveSelectName(string logicalName, IReadOnlyList<DiscoveredAttribute> attributes)
    {
        var attr = attributes.FirstOrDefault(a => a.LogicalName.Equals(logicalName, StringComparison.OrdinalIgnoreCase));
        if (attr is null)
        {
            return logicalName;
        }

        if (attr.AttributeType.Contains("Lookup", StringComparison.OrdinalIgnoreCase) ||
            attr.AttributeType.Contains("Customer", StringComparison.OrdinalIgnoreCase))
        {
            return $"_{attr.LogicalName}_value";
        }

        return attr.LogicalName;
    }

    private static List<DiscoveredAttribute> ParseAttributes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var values))
        {
            return [];
        }

        var list = new List<DiscoveredAttribute>();
        foreach (var item in values.EnumerateArray())
        {
            var logicalName = GetString(item, "LogicalName");
            if (string.IsNullOrWhiteSpace(logicalName))
            {
                continue;
            }

            list.Add(new DiscoveredAttribute
            {
                LogicalName = logicalName,
                SchemaName = GetString(item, "SchemaName"),
                AttributeType = GetString(item, "AttributeType") ?? "Unknown",
                DisplayName = TryGetLocalizedLabel(item, "DisplayName"),
                IsValidForRead = item.TryGetProperty("IsValidForRead", out var read) && read.ValueKind == JsonValueKind.True,
                IsCustomAttribute = item.TryGetProperty("IsCustomAttribute", out var custom) && custom.ValueKind == JsonValueKind.True
            });
        }

        return list.OrderBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? TryGetLocalizedLabel(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var display) ||
            display.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (display.TryGetProperty("UserLocalizedLabel", out var userLabel) &&
            userLabel.ValueKind == JsonValueKind.Object &&
            userLabel.TryGetProperty("Label", out var label) &&
            label.ValueKind == JsonValueKind.String)
        {
            return label.GetString();
        }

        return null;
    }

    private static string BuildReport(
        string logicalName,
        string entitySetName,
        string primaryId,
        string primaryName,
        IReadOnlyList<DiscoveredAttribute> attributes,
        IReadOnlyList<DiscoveredAttribute> custom,
        IReadOnlyList<DiscoveredAttribute> lookups,
        IReadOnlyCollection<string> recommended)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Sales Order Schema Discovery Report");
        sb.AppendLine();
        sb.AppendLine($"Generated (UTC): {DateTimeOffset.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("## Entity");
        sb.AppendLine($"- LogicalName: `{logicalName}`");
        sb.AppendLine($"- EntitySetName: `{entitySetName}`");
        sb.AppendLine($"- PrimaryIdAttribute: `{primaryId}`");
        sb.AppendLine($"- PrimaryNameAttribute: `{primaryName}`");
        sb.AppendLine();
        sb.AppendLine("## Recommended $select fields (for allowlist)");
        foreach (var field in recommended)
        {
            sb.AppendLine($"- `{field}`");
        }

        sb.AppendLine();
        sb.AppendLine("## Custom attributes (IsCustomAttribute=true, readable)");
        if (custom.Count == 0)
        {
            sb.AppendLine("- (none returned)");
        }
        else
        {
            foreach (var attr in custom.Take(100))
            {
                sb.AppendLine($"- `{attr.LogicalName}` ({attr.AttributeType}) — {attr.DisplayName ?? "(no label)"}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Lookup / customer / owner attributes");
        foreach (var attr in lookups.Take(100))
        {
            sb.AppendLine($"- `{attr.LogicalName}` ({attr.AttributeType}) — {attr.DisplayName ?? "(no label)"}");
        }

        sb.AppendLine();
        sb.AppendLine($"## Readable attribute count");
        sb.AppendLine($"- Total readable attributes returned: {attributes.Count}");
        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine("- Field names were read from CRM metadata; they were not guessed.");
        sb.AppendLine("- Lookup values in Web API responses typically appear as `_<logicalname>_value`.");
        sb.AppendLine($"- Previously noted custom field of interest: `{CrmEntityAllowlist.SalesOrderCustomerAccountLookup}` (include only if present above).");
        return sb.ToString();
    }

    private static SalesOrderSchemaDiscoveryResult Fail(string message) =>
        new()
        {
            Success = false,
            Message = message,
            Attributes = [],
            CustomAttributes = [],
            LookupAttributes = [],
            RecommendedSelectFields = []
        };

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}

public sealed class SalesOrderSchemaDiscoveryResult
{
    public bool Success { get; init; }
    public string? LogicalName { get; init; }
    public string? EntitySetName { get; init; }
    public string? PrimaryIdAttribute { get; init; }
    public string? PrimaryNameAttribute { get; init; }
    public IReadOnlyList<DiscoveredAttribute> Attributes { get; init; } = [];
    public IReadOnlyList<DiscoveredAttribute> CustomAttributes { get; init; } = [];
    public IReadOnlyList<DiscoveredAttribute> LookupAttributes { get; init; } = [];
    public IReadOnlyList<string> RecommendedSelectFields { get; init; } = [];
    public string? MarkdownReport { get; init; }
    public string? Message { get; init; }
}

public sealed class DiscoveredAttribute
{
    public required string LogicalName { get; init; }
    public string? SchemaName { get; init; }
    public required string AttributeType { get; init; }
    public string? DisplayName { get; init; }
    public bool IsValidForRead { get; init; }
    public bool IsCustomAttribute { get; init; }
}
