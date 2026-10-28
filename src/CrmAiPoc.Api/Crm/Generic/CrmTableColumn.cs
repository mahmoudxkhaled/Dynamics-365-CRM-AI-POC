namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// UI column definition derived from CRM metadata + validated $select fields.
/// </summary>
public sealed class CrmTableColumn
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; init; }
    public string? LogicalName { get; init; }
}
