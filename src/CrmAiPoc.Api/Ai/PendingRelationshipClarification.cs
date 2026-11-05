using CrmAiPoc.Api.Crm.Generic;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Conversation-scoped pending join clarification. Expires with the session idle TTL.
/// Selection resumes the stored validated plan without re-interpreting via the LLM.
/// </summary>
public sealed class PendingRelationshipClarification
{
    public required CrmQueryPlan Plan { get; init; }
    public required string RootEntity { get; init; }
    public required string RelatedEntity { get; init; }
    public required string OriginalUserMessage { get; init; }
    public required IReadOnlyList<RelationshipClarificationOption> Options { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class RelationshipClarificationOption
{
    public required int Index { get; init; }
    public required string SchemaName { get; init; }
    public required string Description { get; init; }
}
