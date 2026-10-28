namespace CrmAiPoc.Api.Models;

public sealed class ChatResponseDto
{
    public required string ConversationId { get; init; }
    public required string Reply { get; init; }
    public string? ProviderHint { get; init; }
    public Guid? LastReferencedAccountId { get; init; }
    public string? LastReferencedAccountName { get; init; }

    /// <summary>True when CRM tool results were formatted locally and not sent back to the LLM.</summary>
    public bool PrivacySafeToolExecution { get; init; }

    public string? SelectedTool { get; init; }
    public object? StructuredData { get; init; }
    public Guid? CrmWhoAmIUserId { get; init; }
    public bool OutboundAiContainedCrmRecords { get; init; }
    public string? CrmDataProvider { get; init; }

    /// <summary>Correlation id for the Execution Inspector (Development only).</summary>
    public string? ExecutionId { get; init; }
}
