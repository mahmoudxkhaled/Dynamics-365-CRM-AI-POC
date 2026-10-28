using System.Collections.Concurrent;

namespace CrmAiPoc.Api.Ai;

public sealed class ConversationStore
{
    private readonly ConcurrentDictionary<string, ConversationSession> _sessions = new(StringComparer.Ordinal);
    private static readonly TimeSpan IdleTimeToLive = TimeSpan.FromHours(2);

    public ConversationSession GetOrCreate(string? conversationId)
    {
        CleanupExpired();

        var id = string.IsNullOrWhiteSpace(conversationId)
            ? Guid.NewGuid().ToString("N")
            : conversationId.Trim();

        return _sessions.GetOrAdd(id, static key => new ConversationSession(key));
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleTimeToLive;
        foreach (var pair in _sessions)
        {
            if (pair.Value.LastActivityUtc < cutoff)
            {
                _sessions.TryRemove(pair.Key, out _);
            }
        }
    }
}
