namespace CrmAiPoc.Api.Models;

public sealed class ChatResponseDto
{
    public required string ConversationId { get; init; }
    public required string Reply { get; init; }
    public string? ProviderHint { get; init; }

    /// <summary>
    /// "answer" for normal replies / CRM results; "clarification" when suggestions are returned.
    /// Null is treated as answer for backward compatibility.
    /// </summary>
    public string? ResponseType { get; init; }

    /// <summary>Present when <see cref="ResponseType"/> is clarification.</summary>
    public IReadOnlyList<ClarificationSuggestionDto>? Suggestions { get; init; }

    /// <summary>True when CRM tool results were formatted locally and not sent back to the LLM.</summary>
    public bool PrivacySafeToolExecution { get; init; }

    public string? SelectedTool { get; init; }
    public object? StructuredData { get; init; }
    public Guid? CrmWhoAmIUserId { get; init; }
    public bool OutboundAiContainedCrmRecords { get; init; }
    public string? CrmDataProvider { get; init; }
}
