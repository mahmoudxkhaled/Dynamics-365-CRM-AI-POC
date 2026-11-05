namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Tracks conversation id + activity for TTL.
/// Chat history is not resent to the AI. Partial query plans are never stored for resume.
/// </summary>
public sealed class ConversationSession
{
    public string ConversationId { get; }
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

    public ConversationSession(string conversationId)
    {
        ConversationId = conversationId;
    }

    public void Touch() => LastActivityUtc = DateTimeOffset.UtcNow;
}
