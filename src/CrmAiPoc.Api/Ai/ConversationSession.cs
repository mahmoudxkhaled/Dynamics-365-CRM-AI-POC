namespace CrmAiPoc.Api.Ai;

/// <summary>Tracks conversation id + activity for TTL; chat history is not resent to the AI.</summary>
public sealed class ConversationSession
{
    private readonly object _gate = new();

    public string ConversationId { get; }
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Pending relationship selection for a previously validated query plan.</summary>
    public PendingRelationshipClarification? PendingRelationship { get; private set; }

    public ConversationSession(string conversationId)
    {
        ConversationId = conversationId;
    }

    public void Touch() => LastActivityUtc = DateTimeOffset.UtcNow;

    public void SetPendingRelationship(PendingRelationshipClarification pending)
    {
        lock (_gate)
        {
            PendingRelationship = pending;
            Touch();
        }
    }

    public void ClearPendingRelationship()
    {
        lock (_gate)
        {
            PendingRelationship = null;
            Touch();
        }
    }
}
