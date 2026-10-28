using Microsoft.Extensions.AI;

namespace CrmAiPoc.Api.Ai;

public sealed class ConversationSession
{
    private readonly List<ChatMessage> _messages = [];
    private readonly object _gate = new();

    public string ConversationId { get; }
    public Guid? LastReferencedAccountId { get; set; }
    public string? LastReferencedAccountName { get; set; }
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

    public ConversationSession(string conversationId)
    {
        ConversationId = conversationId;
    }

    public IReadOnlyList<ChatMessage> GetMessages()
    {
        lock (_gate)
        {
            return _messages.ToList();
        }
    }

    public void Add(ChatMessage message)
    {
        lock (_gate)
        {
            _messages.Add(message);
            TrimUnlocked(20);
            LastActivityUtc = DateTimeOffset.UtcNow;
        }
    }

    public void ReplaceWith(IEnumerable<ChatMessage> messages)
    {
        lock (_gate)
        {
            _messages.Clear();
            _messages.AddRange(messages);
            TrimUnlocked(20);
            LastActivityUtc = DateTimeOffset.UtcNow;
        }
    }

    private void TrimUnlocked(int keep)
    {
        // Keep system + recent turns.
        if (_messages.Count <= keep)
        {
            return;
        }

        var system = _messages.Where(m => m.Role == ChatRole.System).ToList();
        var rest = _messages.Where(m => m.Role != ChatRole.System).TakeLast(keep - system.Count).ToList();
        _messages.Clear();
        _messages.AddRange(system);
        _messages.AddRange(rest);
    }
}
