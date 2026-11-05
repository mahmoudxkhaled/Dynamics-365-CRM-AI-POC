using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CrmAiPoc.Api.Crm.Metadata;

public enum CrmEntityResolutionStatus
{
    /// <summary>Exactly one approved entity matched (deterministic or validated AI proposal).</summary>
    Resolved = 0,
    /// <summary>Multiple approved entities matched the user text equally well.</summary>
    Ambiguous = 1,
    /// <summary>No deterministic match and no usable AI proposal.</summary>
    Unresolved = 2,
    /// <summary>AI or user proposed an entity outside ApprovedEntities.</summary>
    Unauthorized = 3
}

public enum CrmEntityResolutionSource
{
    None = 0,
    LogicalName,
    DisplayName,
    Alias,
    EntitySetName,
    AiCatalogProposal
}

/// <summary>
/// Hybrid metadata-driven entity resolution:
/// 1) exact/normalized match on logical name, display name, entity set, configured aliases;
/// 2) if none, accept AI tool-call proposal only when it validates against ApprovedEntities.
/// Never invents logical names.
/// </summary>
public static partial class CrmEntityResolver
{
    public static CrmEntityResolutionResult Resolve(
        string? userMessage,
        string? aiProposedEntity,
        IReadOnlyList<CrmEntityMetadata> approvedEntities,
        EntityApprovalPolicy policy,
        string languageHint = "en")
    {
        var catalog = BuildDescriptors(approvedEntities, policy);
        return ResolveCore(userMessage, aiProposedEntity, catalog, policy, languageHint);
    }

    /// <summary>
    /// Resolve using catalog DTOs already built for the AI (no extra metadata load).
    /// </summary>
    public static CrmEntityResolutionResult ResolveFromCatalog(
        string? userMessage,
        string? aiProposedEntity,
        IReadOnlyList<CrmAiCatalogEntity> catalogEntities,
        EntityApprovalPolicy policy,
        string languageHint = "en")
    {
        var catalog = catalogEntities
            .Where(e => policy.IsApproved(e.LogicalName))
            .Select(e => new EntityDescriptor(
                e.LogicalName,
                e.DisplayName,
                e.EntitySetName,
                e.Aliases ?? Array.Empty<string>()))
            .ToArray();
        return ResolveCore(userMessage, aiProposedEntity, catalog, policy, languageHint);
    }

    /// <summary>
    /// Deterministic mentions of approved entities in the user message (longest phrase wins per span).
    /// </summary>
    public static IReadOnlyList<string> FindMentionedEntities(
        string? userMessage,
        IReadOnlyList<CrmEntityMetadata> approvedEntities,
        EntityApprovalPolicy policy)
    {
        var catalog = BuildDescriptors(approvedEntities, policy);
        return FindMentionedLogicalNamesOrdered(userMessage, catalog);
    }

    public static IReadOnlyList<string> FindMentionedEntitiesFromCatalog(
        string? userMessage,
        IReadOnlyList<CrmAiCatalogEntity> catalogEntities,
        EntityApprovalPolicy policy)
    {
        var catalog = catalogEntities
            .Where(e => policy.IsApproved(e.LogicalName))
            .Select(e => new EntityDescriptor(
                e.LogicalName,
                e.DisplayName,
                e.EntitySetName,
                e.Aliases ?? Array.Empty<string>()))
            .ToArray();
        return FindMentionedLogicalNamesOrdered(userMessage, catalog);
    }

    private static CrmEntityResolutionResult ResolveCore(
        string? userMessage,
        string? aiProposedEntity,
        IReadOnlyList<EntityDescriptor> catalog,
        EntityApprovalPolicy policy,
        string languageHint)
    {
        _ = languageHint;
        var orderedMentions = FindMentionedLogicalNamesOrdered(userMessage, catalog);

        if (orderedMentions.Count == 1)
        {
            var match = catalog.First(c => c.LogicalName.Equals(orderedMentions[0], StringComparison.OrdinalIgnoreCase));
            var source = ClassifySource(userMessage, match);
            return CrmEntityResolutionResult.Resolved(match.LogicalName, source);
        }

        if (orderedMentions.Count > 1)
        {
            // Peer choice ("accounts or contacts") is genuinely ambiguous.
            if (LooksLikePeerEntityChoice(userMessage, orderedMentions, catalog))
            {
                return CrmEntityResolutionResult.Ambiguous(
                    orderedMentions,
                    $"Your question mentions multiple approved entities ({string.Join(", ", orderedMentions)}). Which entity should I query?");
            }

            // Result-shape scoring: pick the entity that is the requested record grain,
            // not merely the leftmost mention (e.g. Account names for Sales Orders → salesorder).
            var primary = SelectPrimaryByResultShape(userMessage, orderedMentions, catalog);
            if (primary is null)
            {
                return CrmEntityResolutionResult.Ambiguous(
                    orderedMentions,
                    $"I could not determine the primary result entity among ({string.Join(", ", orderedMentions)}). Which records should be returned?");
            }

            var primaryMatch = catalog.First(c => c.LogicalName.Equals(primary, StringComparison.OrdinalIgnoreCase));
            return CrmEntityResolutionResult.Resolved(primary, ClassifySource(userMessage, primaryMatch));
        }

        // No deterministic match — reuse AI semantic choice from the existing tool call / catalog.
        if (!string.IsNullOrWhiteSpace(aiProposedEntity))
        {
            var proposed = aiProposedEntity.Trim();
            if (policy.TryResolveEntityAlias(proposed, out var fromAlias))
            {
                return CrmEntityResolutionResult.Resolved(fromAlias, CrmEntityResolutionSource.AiCatalogProposal);
            }

            if (policy.IsApproved(proposed))
            {
                var canonical = policy.ApprovedLogicalNames
                    .First(n => n.Equals(proposed, StringComparison.OrdinalIgnoreCase));
                return CrmEntityResolutionResult.Resolved(canonical, CrmEntityResolutionSource.AiCatalogProposal);
            }

            return CrmEntityResolutionResult.Unauthorized(
                proposed,
                $"Entity '{proposed}' is not an approved AI CRM entity. Approved: {string.Join(", ", policy.ApprovedLogicalNames)}.");
        }

        return CrmEntityResolutionResult.Unresolved(
            "I could not determine an approved CRM entity from your question. Please clarify whether you mean accounts, contacts, sales orders, or leads.");
    }

    /// <summary>Returns distinct logical names ordered by first appearance in the message.</summary>
    private static IReadOnlyList<string> FindMentionedLogicalNamesOrdered(
        string? userMessage,
        IReadOnlyList<EntityDescriptor> catalog)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || catalog.Count == 0)
        {
            return Array.Empty<string>();
        }

        var normalizedMessage = Normalize(userMessage);
        if (string.IsNullOrWhiteSpace(normalizedMessage))
        {
            return Array.Empty<string>();
        }

        // Longest phrases first so "sales order" wins over a bare "order" alias if present.
        var phrases = catalog
            .SelectMany(e => e.AllPhrases.Select(p => (Entity: e.LogicalName, Phrase: p, Length: p.Length)))
            .Where(x => x.Length > 0)
            .OrderByDescending(x => x.Length)
            .ThenBy(x => x.Entity, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var firstIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var consumed = new bool[normalizedMessage.Length];

        foreach (var (entity, phrase, _) in phrases)
        {
            var idx = 0;
            while (idx <= normalizedMessage.Length - phrase.Length)
            {
                var at = normalizedMessage.IndexOf(phrase, idx, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                if (IsBoundaryMatch(normalizedMessage, at, phrase.Length) &&
                    !RangeConsumed(consumed, at, phrase.Length))
                {
                    if (!firstIndex.ContainsKey(entity) || at < firstIndex[entity])
                    {
                        firstIndex[entity] = at;
                    }

                    MarkConsumed(consumed, at, phrase.Length);
                    break;
                }

                idx = at + 1;
            }
        }

        return firstIndex
            .OrderBy(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToArray();
    }

    private static bool LooksLikePeerEntityChoice(
        string? userMessage,
        IReadOnlyList<string> mentions,
        IReadOnlyList<EntityDescriptor> catalog)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || mentions.Count < 2)
        {
            return false;
        }

        var norm = Normalize(userMessage);
        // "accounts or contacts" / "accounts and contacts count"
        if (!PeerChoiceRegex().IsMatch(userMessage) && !norm.Contains(" or ", StringComparison.Ordinal))
        {
            // Still peer if "and" joins two entity phrases without a for/of relation cue.
            if (!norm.Contains(" and ", StringComparison.Ordinal))
            {
                return false;
            }
        }

        // Relation cues mean primary+related, not peer choice.
        if (RelationCueRegex().IsMatch(userMessage))
        {
            return false;
        }

        // Explicit "latest/last N <entity>" grain is a projection request, not peer choice.
        foreach (var logical in mentions)
        {
            var match = catalog.First(c => c.LogicalName.Equals(logical, StringComparison.OrdinalIgnoreCase));
            foreach (var phrase in match.AllPhrases.OrderByDescending(p => p.Length))
            {
                if (IsLatestQualifiedPhrase(norm, phrase) ||
                    (TryFindEarliestPhrase(norm, [phrase], out var at, out var length) &&
                     HasExplicitQuantityGrain(at > 0 ? norm[..at] : string.Empty)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Scores mentioned entities by requested result grain (list/count/latest set vs related field projection).
    /// Returns null when scores tie without a decisive shape signal.
    /// </summary>
    private static string? SelectPrimaryByResultShape(
        string? userMessage,
        IReadOnlyList<string> mentions,
        IReadOnlyList<EntityDescriptor> catalog)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || mentions.Count == 0)
        {
            return null;
        }

        var norm = Normalize(userMessage);
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < mentions.Count; i++)
        {
            var logical = mentions[i];
            var match = catalog.First(c => c.LogicalName.Equals(logical, StringComparison.OrdinalIgnoreCase));
            // Score once at the earliest mention span for this entity (avoid multi-phrase inflation).
            if (!TryFindEarliestPhrase(norm, match.AllPhrases, out var at, out var length))
            {
                scores[logical] = (mentions.Count - i) * 0.05;
                continue;
            }

            var before = at > 0 ? norm[..at].TrimEnd() : string.Empty;
            var after = at + length < norm.Length ? norm[(at + length)..].TrimStart() : string.Empty;
            double score = (mentions.Count - i) * 0.05; // tiny leftmost bias only

            // Explicit quantity grain: "last 5 sales orders" / "top 5 …"
            if (HasExplicitQuantityGrain(before))
            {
                score += 8.0;
            }

            if (EndsWithResultSetCue(before) || StartsWithResultSetCue(after))
            {
                score += 4.0;
            }
            else if (ResultSetCueAfterRegex().IsMatch(after))
            {
                score += 1.5;
            }

            // Related-field projection: only when name qualifies THIS entity locally.
            // Do NOT scan a wide window — later "and Account name" must not penalize Sales Orders.
            if (IsLocalNameProjection(before, after))
            {
                score -= 4.0;
            }

            if (RelationPrefixBefore(before))
            {
                score -= 1.5;
            }

            if (RelationSuffixAfter(after))
            {
                score += 2.5;
            }

            // "Primary Contact" / "Customer Account" style compound labels are related projections.
            if (IsCompoundRelatedLabel(before, after, match.AllPhrases))
            {
                score -= 2.0;
            }

            scores[logical] = score;
        }

        // Global pattern boost: entity after "latest/last/recent" is the record grain.
        foreach (var logical in mentions)
        {
            var match = catalog.First(c => c.LogicalName.Equals(logical, StringComparison.OrdinalIgnoreCase));
            foreach (var phrase in match.AllPhrases.OrderByDescending(p => p.Length))
            {
                if (IsLatestQualifiedPhrase(norm, phrase))
                {
                    scores[logical] += 5.0;
                    break;
                }
            }
        }

        // Decisive: exactly one entity carries an explicit quantity/latest grain cue.
        var grainLeaders = scores.Where(kv => kv.Value >= 6.0).Select(kv => kv.Key).ToList();
        if (grainLeaders.Count == 1)
        {
            return grainLeaders[0];
        }

        var ranked = scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => mentions.ToList().IndexOf(kv.Key))
            .ToList();

        if (ranked.Count == 0)
        {
            return mentions[0];
        }

        if (ranked.Count >= 2 && Math.Abs(ranked[0].Value - ranked[1].Value) < 0.75)
        {
            return null;
        }

        return ranked[0].Key;
    }

    private static bool HasExplicitQuantityGrain(string before)
    {
        // "last 5" / "top 5" immediately before the entity phrase.
        return ExplicitQuantityGrainRegex().IsMatch(before);
    }

    /// <summary>
    /// True when name qualifies this entity (Account names / Customer Account name),
    /// not when a later clause projects a different entity's name.
    /// </summary>
    private static bool IsLocalNameProjection(string before, string after)
    {
        // Immediate left: "names of", "name of the"
        if (before.EndsWith(" name", StringComparison.Ordinal) ||
            before.EndsWith(" names", StringComparison.Ordinal) ||
            NameProjectionEndRegex().IsMatch(before))
        {
            return true;
        }

        // Immediate right only for short residue like "names" / "name" (not a whole later clause).
        var afterToken = after.Split(' ', 2)[0];
        return afterToken is "name" or "names";
    }

    private static bool IsCompoundRelatedLabel(
        string before,
        string after,
        IReadOnlyList<string> phrases)
    {
        // "customer account", "primary contact" often appear with name.
        _ = phrases;
        var left = before.Length > 40 ? before[^40..] : before;
        return (left.Contains("customer", StringComparison.Ordinal) ||
                left.Contains("primary", StringComparison.Ordinal)) &&
               IsLocalNameProjection(before, after);
    }

    private static bool IsLatestQualifiedPhrase(string norm, string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase) || !norm.Contains(phrase, StringComparison.Ordinal))
        {
            return false;
        }

        var at = norm.IndexOf(phrase, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        var before = norm[..at];
        // English: "... latest|last|recent <entity>"
        return before.EndsWith(" latest ", StringComparison.Ordinal) ||
               before.EndsWith(" last ", StringComparison.Ordinal) ||
               before.EndsWith(" recent ", StringComparison.Ordinal) ||
               before.EndsWith("latest ", StringComparison.Ordinal) ||
               before.EndsWith("last ", StringComparison.Ordinal) ||
               HasExplicitQuantityGrain(before);
    }

    private static bool TryFindEarliestPhrase(
        string normalizedMessage,
        IReadOnlyList<string> phrases,
        out int start,
        out int length)
    {
        start = -1;
        length = 0;
        var best = int.MaxValue;
        foreach (var phrase in phrases.Where(p => p.Length >= 2).OrderByDescending(p => p.Length))
        {
            var idx = 0;
            while (idx <= normalizedMessage.Length - phrase.Length)
            {
                var at = normalizedMessage.IndexOf(phrase, idx, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                if (IsBoundaryMatch(normalizedMessage, at, phrase.Length) && at < best)
                {
                    best = at;
                    start = at;
                    length = phrase.Length;
                    break;
                }

                idx = at + 1;
            }
        }

        return start >= 0;
    }

    private static bool EndsWithResultSetCue(string before) =>
        ResultSetCueEndRegex().IsMatch(before);

    private static bool StartsWithResultSetCue(string after) =>
        ResultSetCueStartRegex().IsMatch(after);

    private static bool NameProjectionNear(string before, string after) =>
        NameProjectionRegex().IsMatch(before) || NameProjectionRegex().IsMatch(after) ||
        before.EndsWith(" name", StringComparison.Ordinal) ||
        before.EndsWith(" names", StringComparison.Ordinal) ||
        after.StartsWith("name", StringComparison.Ordinal) ||
        after.StartsWith("names", StringComparison.Ordinal);

    private static bool RelationPrefixBefore(string before) =>
        RelationPrefixEndRegex().IsMatch(before);

    private static bool RelationSuffixAfter(string after) =>
        RelationSuffixStartRegex().IsMatch(after);

    private static CrmEntityResolutionSource ClassifySource(string? message, EntityDescriptor match)
    {
        var norm = Normalize(message);
        if (ContainsPhrase(norm, Normalize(match.LogicalName)))
        {
            return CrmEntityResolutionSource.LogicalName;
        }

        if (!string.IsNullOrWhiteSpace(match.EntitySetName) &&
            ContainsPhrase(norm, Normalize(match.EntitySetName)))
        {
            return CrmEntityResolutionSource.EntitySetName;
        }

        if (!string.IsNullOrWhiteSpace(match.DisplayName) &&
            ContainsPhrase(norm, Normalize(match.DisplayName)))
        {
            return CrmEntityResolutionSource.DisplayName;
        }

        return CrmEntityResolutionSource.Alias;
    }

    private static bool ContainsPhrase(string haystack, string phrase) =>
        !string.IsNullOrWhiteSpace(phrase) &&
        haystack.IndexOf(phrase, StringComparison.Ordinal) >= 0;

    private static IReadOnlyList<EntityDescriptor> BuildDescriptors(
        IReadOnlyList<CrmEntityMetadata> approvedEntities,
        EntityApprovalPolicy policy)
    {
        var list = new List<EntityDescriptor>();
        foreach (var entity in approvedEntities.Where(e => policy.IsApproved(e.LogicalName) && e.IsApprovedForAi))
        {
            var aliases = policy.GetAliases(entity.LogicalName)
                .Concat(entity.Aliases)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            list.Add(new EntityDescriptor(
                entity.LogicalName,
                entity.DisplayName,
                entity.EntitySetName,
                aliases));
        }

        // Ensure every ApprovedEntities entry is resolvable even if metadata stub is missing.
        foreach (var logical in policy.ApprovedLogicalNames)
        {
            if (list.Any(e => e.LogicalName.Equals(logical, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            list.Add(new EntityDescriptor(
                logical,
                logical,
                logical + "s",
                policy.GetAliases(logical).ToArray()));
        }

        return list;
    }

    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var prevSpace = false;
        foreach (var ch in text.Trim().Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-')
            {
                sb.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
                prevSpace = false;
            }
            else if (char.IsWhiteSpace(ch) || ch is '\'' or '"' or '،' or ',' or '.' or ':' or ';' or '؟' or '?')
            {
                if (!prevSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            // drop other punctuation
        }

        return sb.ToString().Trim();
    }

    private static bool IsBoundaryMatch(string haystack, int start, int length)
    {
        var beforeOk = start == 0 || !char.IsLetterOrDigit(haystack[start - 1]);
        var end = start + length;
        var afterOk = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);
        return beforeOk && afterOk;
    }

    private static bool RangeConsumed(bool[] consumed, int start, int length)
    {
        for (var i = start; i < start + length && i < consumed.Length; i++)
        {
            if (consumed[i])
            {
                return true;
            }
        }

        return false;
    }

    private static void MarkConsumed(bool[] consumed, int start, int length)
    {
        for (var i = start; i < start + length && i < consumed.Length; i++)
        {
            consumed[i] = true;
        }
    }

    private sealed class EntityDescriptor
    {
        public EntityDescriptor(
            string logicalName,
            string? displayName,
            string? entitySetName,
            IReadOnlyList<string> aliases)
        {
            LogicalName = logicalName;
            DisplayName = displayName;
            EntitySetName = entitySetName;
            var phrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPhrase(phrases, logicalName);
            AddPhrase(phrases, displayName);
            AddPhrase(phrases, entitySetName);
            foreach (var alias in aliases)
            {
                AddPhrase(phrases, alias);
            }

            // Compact form without spaces for "salesorder" vs "sales order".
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                AddPhrase(phrases, Compact(displayName));
            }

            AllPhrases = phrases
                .Select(Normalize)
                .Where(p => p.Length >= 2)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        public string LogicalName { get; }
        public string? DisplayName { get; }
        public string? EntitySetName { get; }
        public IReadOnlyList<string> AllPhrases { get; }

        private static void AddPhrase(HashSet<string> set, string? raw)
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                set.Add(raw.Trim());
            }
        }

        private static string Compact(string value) =>
            WhitespaceRegex().Replace(value, string.Empty);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(
        @"\bor\b|\band\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PeerChoiceRegex();

    [GeneratedRegex(
        @"\bfor\b|\bof\b|\bwith\b|\bincluding\b|\brelated\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelationCueRegex();

    [GeneratedRegex(
        @"\b(show|list|get|retrieve|find|count|how\s+many|latest|last|recent|top)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResultSetCueBeforeRegex();

    [GeneratedRegex(
        @"\b(count|total|grouped|by\s+status)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResultSetCueAfterRegex();

    [GeneratedRegex(
        @"(?:^|.*\b)(show|list|get|retrieve|find|count|how\s+many|latest|last|recent|top)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResultSetCueEndRegex();

    [GeneratedRegex(
        @"(?:last|latest|top)\s+\d{1,2}\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitQuantityGrainRegex();

    [GeneratedRegex(
        @"(?:name|names)\s+(?:the\s+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NameProjectionEndRegex();

    [GeneratedRegex(
        @"^(count|total|grouped)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResultSetCueStartRegex();

    [GeneratedRegex(
        @"\b(name|names|fullname|display\s*name)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NameProjectionRegex();

    [GeneratedRegex(
        @"(?:^|.*\b)(for|of|with|related\s+to)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelationPrefixEndRegex();

    [GeneratedRegex(
        @"^(for|of|with|related\s+to)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelationSuffixStartRegex();
}

public sealed class CrmEntityResolutionResult
{
    public CrmEntityResolutionStatus Status { get; init; }
    public string? LogicalName { get; init; }
    public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();
    public CrmEntityResolutionSource Source { get; init; }
    public string? Message { get; init; }

    public static CrmEntityResolutionResult Resolved(string logicalName, CrmEntityResolutionSource source) =>
        new()
        {
            Status = CrmEntityResolutionStatus.Resolved,
            LogicalName = logicalName,
            Source = source,
            Candidates = [logicalName]
        };

    public static CrmEntityResolutionResult Ambiguous(IReadOnlyList<string> candidates, string message) =>
        new()
        {
            Status = CrmEntityResolutionStatus.Ambiguous,
            Candidates = candidates,
            Message = message
        };

    public static CrmEntityResolutionResult Unresolved(string message) =>
        new()
        {
            Status = CrmEntityResolutionStatus.Unresolved,
            Message = message
        };

    public static CrmEntityResolutionResult Unauthorized(string proposed, string message) =>
        new()
        {
            Status = CrmEntityResolutionStatus.Unauthorized,
            LogicalName = proposed,
            Message = message
        };
}
