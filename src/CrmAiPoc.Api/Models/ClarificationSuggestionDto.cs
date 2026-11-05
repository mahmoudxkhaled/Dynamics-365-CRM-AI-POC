namespace CrmAiPoc.Api.Models;

/// <summary>Clickable clarification option returned when the AI question is ambiguous.</summary>
public sealed class ClarificationSuggestionDto
{
    /// <summary>Short UI label (e.g. "By Created On").</summary>
    public required string Label { get; init; }

    /// <summary>Complete standalone question submitted on click via /api/chat.</summary>
    public required string Question { get; init; }
}
