namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Role of a stored conversation turn used for privacy-safe Groq context.
/// </summary>
public enum ConversationTurnRole
{
    User = 0,
    Assistant = 1
}

/// <summary>
/// One stored dialogue turn. Assistant turns are backend-allowlisted only (never CRM records/tools).
/// </summary>
public sealed class ConversationTurn
{
    public required ConversationTurnRole Role { get; init; }

    /// <summary>Plain text content shared with Groq when selected into the outbound window.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Backend kind: <c>user</c>, <c>clarification</c>, <c>unsupported</c>, or <c>answer</c>.
    /// </summary>
    public required string Kind { get; init; }
}

/// <summary>
/// Tracks conversation id, activity for TTL, and ordered privacy-safe dialogue turns.
/// CRM records, tool payloads, and FetchXML results are never stored for LLM context.
/// </summary>
public sealed class ConversationSession
{
    private const int MaxStoredTurns = 40;

    private readonly object _gate = new();
    private readonly List<ConversationTurn> _turns = [];

    public string ConversationId { get; }
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

    public ConversationSession(string conversationId)
    {
        ConversationId = conversationId;
    }

    public void Touch() => LastActivityUtc = DateTimeOffset.UtcNow;

    /// <summary>
    /// Snapshot of stored turns in chronological order (oldest first).
    /// </summary>
    public IReadOnlyList<ConversationTurn> GetTurnsSnapshot()
    {
        lock (_gate)
        {
            return _turns.ToArray();
        }
    }

    /// <summary>
    /// Snapshot of stored user-question text only (oldest first). Kept for diagnostics/tests.
    /// </summary>
    public IReadOnlyList<string> GetUserQuestionsSnapshot()
    {
        lock (_gate)
        {
            return _turns
                .Where(t => t.Role == ConversationTurnRole.User)
                .Select(t => t.Text)
                .ToArray();
        }
    }

    public int UserQuestionCount
    {
        get
        {
            lock (_gate)
            {
                return _turns.Count(t => t.Role == ConversationTurnRole.User);
            }
        }
    }

    public int TurnCount
    {
        get
        {
            lock (_gate)
            {
                return _turns.Count;
            }
        }
    }

    /// <summary>
    /// Appends an accepted user question. Skips empty text and consecutive exact duplicates
    /// (helps avoid double-append on client retries of the same message).
    /// </summary>
    public bool TryAppendUserQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return false;
        }

        var trimmed = question.Trim();
        lock (_gate)
        {
            if (_turns.Count > 0 &&
                _turns[^1].Role == ConversationTurnRole.User &&
                string.Equals(_turns[^1].Text, trimmed, StringComparison.Ordinal))
            {
                return false;
            }

            _turns.Add(new ConversationTurn
            {
                Role = ConversationTurnRole.User,
                Text = trimmed,
                Kind = "user"
            });
            TrimLocked();
            return true;
        }
    }

    /// <summary>
    /// Appends a backend-allowlisted assistant turn. Caller must already enforce privacy rules.
    /// </summary>
    public bool TryAppendSafeAssistantTurn(string kind, string text)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmedKind = kind.Trim().ToLowerInvariant();
        var trimmedText = text.Trim();
        lock (_gate)
        {
            _turns.Add(new ConversationTurn
            {
                Role = ConversationTurnRole.Assistant,
                Text = trimmedText,
                Kind = trimmedKind
            });
            TrimLocked();
            return true;
        }
    }

    private void TrimLocked()
    {
        while (_turns.Count > MaxStoredTurns)
        {
            _turns.RemoveAt(0);
        }
    }
}
