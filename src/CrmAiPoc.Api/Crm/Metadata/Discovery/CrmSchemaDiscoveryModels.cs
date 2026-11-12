namespace CrmAiPoc.Api.Crm.Metadata.Discovery;

public sealed class CrmSchemaDiscoveryRequest
{
    public IReadOnlyList<string> Entities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Concepts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<(string Left, string Right)> RelationshipPairs { get; init; } =
        Array.Empty<(string, string)>();
}

public sealed class CrmSchemaDiscoveryResult
{
    public IReadOnlyList<CrmSchemaFieldMatch> Fields { get; init; } = Array.Empty<CrmSchemaFieldMatch>();
    public IReadOnlyList<CrmSchemaRelationshipMatch> Relationships { get; init; } =
        Array.Empty<CrmSchemaRelationshipMatch>();
    /// <summary>
    /// Compact identity metadata for scoped entities (primary id/name) so planners can
    /// group aggregates by stable record keys without receiving CRM records.
    /// </summary>
    public IReadOnlyList<CrmSchemaEntityIdentity> Entities { get; init; } =
        Array.Empty<CrmSchemaEntityIdentity>();
    public IReadOnlyList<string> UnresolvedConcepts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public int ResponseCharacters { get; init; }
}

public sealed class CrmSchemaEntityIdentity
{
    public required string LogicalName { get; init; }
    public string? PrimaryIdAttribute { get; init; }
    public string? PrimaryNameAttribute { get; init; }
}

public sealed class CrmSchemaFieldMatch
{
    public required string Concept { get; init; }
    public required string Entity { get; init; }
    public bool LowConfidence { get; init; }
    public IReadOnlyList<CrmSchemaFieldCandidate> Candidates { get; init; } =
        Array.Empty<CrmSchemaFieldCandidate>();
}

public sealed class CrmSchemaFieldCandidate
{
    public required string LogicalName { get; init; }
    public string? DisplayName { get; init; }
    public required string Type { get; init; }
    public IReadOnlyList<string> LookupTargets { get; init; } = Array.Empty<string>();
    public int Score { get; init; }

    /// <summary>
    /// Choice / OptionSet labels and numeric values when this attribute is a picklist, state, status, or boolean.
    /// Populated lazily only for returned candidates — never the full catalog.
    /// </summary>
    public IReadOnlyList<CrmSchemaOptionValue>? Options { get; init; }

    /// <summary>
    /// Set when OptionSet enrichment failed for a choice attribute.
    /// Distinguishes Dynamics retrieval failure from an empty OptionSet.
    /// </summary>
    public string? OptionsLoadError { get; init; }
}

public sealed class CrmSchemaOptionValue
{
    public required int Value { get; init; }
    public required string Label { get; init; }
}

public sealed class CrmSchemaRelationshipMatch
{
    public required string FromEntity { get; init; }
    public required string ToEntity { get; init; }
    public IReadOnlyList<CrmSchemaRelationshipCandidate> Candidates { get; init; } =
        Array.Empty<CrmSchemaRelationshipCandidate>();
}

public sealed class CrmSchemaRelationshipCandidate
{
    public required string SchemaName { get; init; }
    public required string ReferencingEntity { get; init; }
    public string? ReferencingAttribute { get; init; }
    public required string ReferencedEntity { get; init; }
    public string? ReferencedAttribute { get; init; }
    public string? LookupDisplayLabel { get; init; }
    public string? BusinessLabel { get; init; }
    public string Kind { get; init; } = "ManyToOne";
}

public static class CrmSchemaDiscoveryLimits
{
    public const int MaxEntities = 6;
    public const int MaxConcepts = 12;
    public const int MaxRelationshipPairs = 8;
    public const int DefaultMaxCandidatesPerConcept = 5;
    public const int DefaultMaxRelationshipCandidates = 8;
    public const int DefaultMaxDiscoveryCallsPerRequest = 2;
    public const int MinAcceptScore = 25;
    public const int LowConfidenceTopScore = 45;
    public const int MaxOptionsPerAttribute = 40;
}
