using System.ComponentModel.DataAnnotations;

namespace CrmAiPoc.Api.Models;

public sealed class ChatRequestDto
{
    /// <summary>Natural-language CRM question (Arabic, English, or mixed).</summary>
    [Required]
    [MaxLength(4000)]
    public string Message { get; set; } = string.Empty;

    /// <summary>Optional conversation id for follow-up questions.</summary>
    [MaxLength(64)]
    public string? ConversationId { get; set; }
}
