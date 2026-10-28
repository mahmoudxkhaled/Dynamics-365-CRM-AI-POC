using System.ComponentModel.DataAnnotations;

namespace CrmAiPoc.Api.Models;

public sealed class ChatRequestDto
{
    /// <summary>Natural-language question in English or Arabic.</summary>
    [Required]
    [MaxLength(4000)]
    public string Message { get; set; } = string.Empty;

    /// <summary>Optional conversation id for follow-up questions.</summary>
    [MaxLength(64)]
    public string? ConversationId { get; set; }

    /// <summary>
    /// Optional client-generated correlation id so the Execution Inspector can poll while chat runs.
    /// Ignored when the inspector is disabled.
    /// </summary>
    [MaxLength(64)]
    public string? ExecutionId { get; set; }
}
