namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Outcome of lazy OptionSet / Boolean choice metadata loading.
/// Distinguishes "options loaded", "already cached", and "enrichment failed"
/// so callers do not treat a Dynamics HTTP failure as an empty OptionSet.
/// </summary>
public sealed class AttributeOptionsResult
{
    public CrmAttributeMetadata? Attribute { get; init; }

    /// <summary>True when label/value maps are present on the attribute.</summary>
    public bool OptionsAvailable { get; init; }

    /// <summary>True when options were already present in the metadata cache (no Dynamics call).</summary>
    public bool AlreadyCached { get; init; }

    /// <summary>Populated when enrichment failed; never invents empty options as success.</summary>
    public string? Error { get; init; }

    /// <summary>Relative Dynamics Web API path used for the type-specific OptionSet request.</summary>
    public string? RequestPath { get; init; }
}
