namespace CrmAiPoc.Api.Crm.Metadata.Semantic;

/// <summary>Lifecycle of the in-memory CRM metadata embedding index.</summary>
public enum SemanticIndexState
{
    NotStarted = 0,
    Building = 1,
    Ready = 2,
    Failed = 3
}
