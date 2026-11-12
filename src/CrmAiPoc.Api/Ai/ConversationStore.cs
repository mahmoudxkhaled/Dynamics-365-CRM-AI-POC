using System.Collections.Concurrent;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// In-memory conversation sessions keyed by conversationId.
/// Idle sessions (including privacy-safe dialogue history) expire after <see cref="IdleTimeToLive"/>.
/// </summary>
public sealed class ConversationStore
{
    private readonly ConcurrentDictionary<string, ConversationSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Idle TTL for conversation sessions and their privacy-safe history.</summary>
    public static readonly TimeSpan IdleTimeToLive = TimeSpan.FromHours(2);

    public ConversationSession GetOrCreate(string? conversationId)
    {
        CleanupExpired();

        var id = string.IsNullOrWhiteSpace(conversationId)
            ? Guid.NewGuid().ToString("N")
            : conversationId.Trim();

        return _sessions.GetOrAdd(id, static key => new ConversationSession(key));
    }

    /// <summary>Test helper: force removal of idle sessions older than <see cref="IdleTimeToLive"/>.</summary>
    internal void CleanupExpiredForTests() => CleanupExpired();

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
