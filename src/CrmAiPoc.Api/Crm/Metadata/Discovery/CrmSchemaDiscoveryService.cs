using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrmAiPoc.Api.Security;

namespace CrmAiPoc.Api.Crm.Metadata.Discovery;

/// <summary>
/// On-demand CRM schema discovery for Groq.
/// Lexical ranking by default; optional hybrid lexical + Ollama semantic ranking (SEM-02).
/// Searches approved entities/attributes/relationships from the existing metadata cache.
/// Never returns CRM business records.
/// </summary>
public sealed partial class CrmSchemaDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ICrmMetadataService _metadata;
    private readonly EntityApprovalPolicy _policy;
    private readonly ICrmAuthorizationService _auth;
    private readonly HybridMetadataRanker? _hybridRanker;
    private readonly ILogger<CrmSchemaDiscoveryService> _logger;

    public CrmSchemaDiscoveryService(
        ICrmMetadataService metadata,
        EntityApprovalPolicy policy,
        ICrmAuthorizationService auth,
        ILogger<CrmSchemaDiscoveryService> logger,
        HybridMetadataRanker? hybridRanker = null)
    {
        _metadata = metadata;
        _policy = policy;
        _auth = auth;
        _logger = logger;
        _hybridRanker = hybridRanker;
    }

    public async Task<(bool Success, string Json, CrmSchemaDiscoveryResult? Result, string? Error)> DiscoverAsync(
        CrmSchemaDiscoveryRequest request,
        int maxCandidatesPerConcept = CrmSchemaDiscoveryLimits.DefaultMaxCandidatesPerConcept,
        int maxRelationshipCandidates = CrmSchemaDiscoveryLimits.DefaultMaxRelationshipCandidates,
        CancellationToken cancellationToken = default)
    {
        await _metadata.EnsureApprovedEntitiesLoadedAsync(cancellationToken);

        var scope = await ResolveEntityScopeAsync(request, cancellationToken);
        _logger.LogInformation(
            "[METADATA DISCOVERY] EntitiesRequested={Requested} EntitiesScoped={Scoped}",
            request.Entities.Count,
            scope.Count);

        if (scope.Count == 0 && request.Concepts.Count > 0)
        {
            var empty = new CrmSchemaDiscoveryResult
            {
                UnresolvedConcepts = request.Concepts.ToArray(),
                Notes = ["No approved/authorized entities were available for metadata discovery."]
            };
            var emptyJson = Serialize(empty);
            empty = new CrmSchemaDiscoveryResult
            {
                UnresolvedConcepts = empty.UnresolvedConcepts,
                Notes = empty.Notes,
                ResponseCharacters = emptyJson.Length
            };
            return (true, Serialize(empty), empty, null);
        }

        var fields = new List<CrmSchemaFieldMatch>();
        var unresolved = new List<string>();
        var notes = new List<string>();

        foreach (var concept in request.Concepts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("[METADATA SEARCH] Concept={Concept}", Truncate(concept, 80));

            // Lexical first (always). Semantic path focuses compound entity+attribute concepts separately.
            var lexicalPool = RankAttributesForConcept(
                concept,
                scope,
                Math.Max(maxCandidatesPerConcept * 4, 20));

            IReadOnlyList<RankedAttribute> ranked = lexicalPool;
            var retrievalMode = "lexical";
            if (_hybridRanker is not null)
            {
                var hybrid = await _hybridRanker.RankAttributesAsync(
                    concept,
                    scope,
                    lexicalPool,
                    maxCandidatesPerConcept,
                    correlationId: null,
                    cancellationToken);
                ranked = hybrid.Candidates;
                retrievalMode = hybrid.Mode;
                if (!string.IsNullOrWhiteSpace(hybrid.FallbackReason) &&
                    !string.Equals(hybrid.FallbackReason, "hybrid-disabled", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add($"Semantic retrieval fallback for '{concept}': {hybrid.FallbackReason}");
                }
            }
            else
            {
                ranked = lexicalPool.Take(Math.Max(1, maxCandidatesPerConcept)).ToArray();
            }

            if (ranked.Count == 0)
            {
                unresolved.Add(concept);
                _logger.LogInformation(
                    "[METADATA SEARCH] Concept={Concept} Mode={Mode} CandidatesReturned=0 Unresolved=true",
                    Truncate(concept, 80),
                    retrievalMode);
                continue;
            }

            var topScore = ranked[0].Score;
            var lowConfidence = topScore < CrmSchemaDiscoveryLimits.LowConfidenceTopScore;
            if (lowConfidence)
            {
                notes.Add($"Low confidence for concept '{concept}' (topScore={topScore}).");
            }

            // Keep alternatives; do not silently collapse to a single match when close scores exist.
            // Lazily attach OptionSet options only for choice-typed candidates (SEM-03).
            var candidates = new List<CrmSchemaFieldCandidate>(ranked.Count);
            foreach (var r in ranked)
            {
                candidates.Add(await ToFieldCandidateAsync(r, cancellationToken));
            }

            fields.Add(new CrmSchemaFieldMatch
            {
                Concept = concept,
                Entity = ranked[0].EntityLogicalName,
                LowConfidence = lowConfidence,
                Candidates = candidates
            });

            foreach (var c in candidates.Where(c => !string.IsNullOrWhiteSpace(c.OptionsLoadError)))
            {
                notes.Add(
                    $"OptionSet enrichment failed for '{c.LogicalName}' on '{ranked[0].EntityLogicalName}': {c.OptionsLoadError}");
            }

            _logger.LogInformation(
                "[METADATA SEARCH] Concept={Concept} Mode={Mode} CandidatesReturned={Count} Top={Top} Entity={Entity} Score={Score} LowConfidence={Low}",
                Truncate(concept, 80),
                retrievalMode,
                ranked.Count,
                ranked[0].LogicalName,
                ranked[0].EntityLogicalName,
                topScore,
                lowConfidence);
        }

        var relationships = new List<CrmSchemaRelationshipMatch>();
        foreach (var (left, right) in request.RelationshipPairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pair = await DiscoverRelationshipsAsync(left, right, maxRelationshipCandidates, cancellationToken);
            if (pair is not null)
            {
                relationships.Add(pair);
                _logger.LogInformation(
                    "[RELATIONSHIP DISCOVERY] Pair={Left}->{Right} CandidatesReturned={Count}",
                    pair.FromEntity,
                    pair.ToEntity,
                    pair.Candidates.Count);
            }
            else
            {
                notes.Add($"No approved relationships found between '{left}' and '{right}'.");
                _logger.LogInformation(
                    "[RELATIONSHIP DISCOVERY] Pair={Left}->{Right} CandidatesReturned=0",
                    left,
                    right);
            }
        }

        var entityIdentities = scope
            .Select(s => new CrmSchemaEntityIdentity
            {
                LogicalName = s.Entity.LogicalName,
                PrimaryIdAttribute = s.Entity.PrimaryIdAttribute,
                PrimaryNameAttribute = s.Entity.PrimaryNameAttribute
            })
            .ToArray();

        // If entities were requested without concepts, still allow relationship-only discovery.
        var result = new CrmSchemaDiscoveryResult
        {
            Fields = fields,
            Relationships = relationships,
            Entities = entityIdentities,
            UnresolvedConcepts = unresolved,
            Notes = notes
        };

        var json = Serialize(result);
        result = new CrmSchemaDiscoveryResult
        {
            Fields = result.Fields,
            Relationships = result.Relationships,
            Entities = result.Entities,
            UnresolvedConcepts = result.UnresolvedConcepts,
            Notes = result.Notes,
            ResponseCharacters = json.Length
        };
        json = Serialize(result);

        _logger.LogInformation(
            "[METADATA DISCOVERY] ResponseCharacters={Chars} FieldGroups={Fields} RelationshipGroups={Rels} Unresolved={Unresolved}",
            json.Length,
            fields.Count,
            relationships.Count,
            unresolved.Count);

        return (true, json, result, null);
    }

    private async Task<IReadOnlyList<ScopedEntity>> ResolveEntityScopeAsync(
        CrmSchemaDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in request.Entities)
        {
            if (_policy.TryResolveEntityAlias(raw, out var logical) || _policy.IsApproved(raw))
            {
                names.Add(_policy.IsApproved(logical) ? logical : raw.Trim());
            }
        }

        foreach (var (left, right) in request.RelationshipPairs)
        {
            if (_policy.TryResolveEntityAlias(left, out var l) || _policy.IsApproved(left))
            {
                names.Add(_policy.IsApproved(l) ? l : left.Trim());
            }

            if (_policy.TryResolveEntityAlias(right, out var r) || _policy.IsApproved(right))
            {
                names.Add(_policy.IsApproved(r) ? r : right.Trim());
            }
        }

        // When concepts are provided without entities, search all approved entities (bounded).
        if (names.Count == 0 && request.Concepts.Count > 0)
        {
            foreach (var n in _policy.ApprovedLogicalNames.Take(CrmSchemaDiscoveryLimits.MaxEntities))
            {
                names.Add(n);
            }
        }

        var list = new List<ScopedEntity>();
        foreach (var name in names)
        {
            if (!_policy.IsApproved(name))
            {
                continue;
            }

            // Schema discovery is a read-only metadata operation (no CRM records).
            var auth = _auth.AuthorizeRead(name, "read_schema_metadata");
            if (!auth.Allowed)
            {
                continue;
            }

            var entity = await _metadata.GetEntityAsync(name, cancellationToken);
            if (entity is null || !entity.IsApprovedForAi)
            {
                continue;
            }

            list.Add(new ScopedEntity(entity));
        }

        return list;
    }

    private static IReadOnlyList<RankedAttribute> RankAttributesForConcept(
        string concept,
        IReadOnlyList<ScopedEntity> scope,
        int maxCandidates)
    {
        var tokens = Tokenize(concept);
        if (tokens.Count == 0)
        {
            return Array.Empty<RankedAttribute>();
        }

        var conceptNorm = Normalize(concept);
        var scored = new List<RankedAttribute>();

        foreach (var scoped in scope)
        {
            foreach (var attr in scoped.Entity.Attributes)
            {
                if (!attr.IsValidForRead)
                {
                    continue;
                }

                var score = ScoreAttribute(conceptNorm, tokens, scoped.Entity, attr);
                if (score < CrmSchemaDiscoveryLimits.MinAcceptScore)
                {
                    continue;
                }

                scored.Add(new RankedAttribute(
                    scoped.Entity.LogicalName,
                    attr.LogicalName,
                    attr.DisplayName,
                    attr.Kind.ToString(),
                    ResolveLookupTargets(scoped.Entity, attr),
                    score));
            }
        }

        // Stable ranking independent of metadata collection order:
        // score desc, then logical name, then entity.
        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.EntityLogicalName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxCandidates))
            .ToArray();
    }

    internal static int ScoreAttribute(
        string conceptNorm,
        IReadOnlyList<string> tokens,
        CrmEntityMetadata entity,
        CrmAttributeMetadata attr)
    {
        var logical = Normalize(attr.LogicalName);
        var display = Normalize(attr.DisplayName);
        var description = Normalize(attr.Description);
        var aliases = attr.Aliases.Select(Normalize).Where(a => a.Length > 0).ToArray();
        var entityTokens = Tokenize(entity.LogicalName + " " + (entity.DisplayName ?? string.Empty));

        var score = 0;

        if (logical.Equals(conceptNorm, StringComparison.Ordinal) ||
            aliases.Any(a => a.Equals(conceptNorm, StringComparison.Ordinal)))
        {
            score += 100;
        }
        else if (!string.IsNullOrEmpty(display) && display.Equals(conceptNorm, StringComparison.Ordinal))
        {
            score += 95;
        }
        else if (logical.Equals(string.Concat(tokens), StringComparison.Ordinal))
        {
            score += 90;
        }

        var displayHits = tokens.Count(t => display.Contains(t, StringComparison.Ordinal));
        var logicalHits = tokens.Count(t => logical.Contains(t, StringComparison.Ordinal));
        var descriptionHits = string.IsNullOrEmpty(description)
            ? 0
            : tokens.Count(t => description.Contains(t, StringComparison.Ordinal));
        if (tokens.Count > 0 && displayHits == tokens.Count)
        {
            score += 75;
        }
        else if (tokens.Count > 0 && logicalHits == tokens.Count)
        {
            score += 70;
        }
        else
        {
            score += displayHits * 18;
            score += logicalHits * 15;
            score += descriptionHits * 8;
        }

        foreach (var alias in aliases)
        {
            score += tokens.Count(t => alias.Contains(t, StringComparison.Ordinal)) * 10;
        }

        // Soft entity-context alignment (do not require entity name in concept).
        var entityHits = tokens.Count(t => entityTokens.Contains(t, StringComparer.Ordinal));
        score += Math.Min(24, entityHits * 10);

        // Prefer concise / specific labels over verbose near-matches
        // (e.g. "Total Amount" over "Total Pre-Freight Amount").
        var displayTokens = Tokenize(attr.DisplayName);
        var unmatchedDisplay = displayTokens.Count(t => !tokens.Contains(t, StringComparer.Ordinal));
        score -= Math.Min(24, unmatchedDisplay * 5);

        var compactConcept = string.Concat(tokens);
        if (compactConcept.Length >= 4)
        {
            if (logical.Equals(compactConcept, StringComparison.Ordinal) ||
                display.Replace(" ", "", StringComparison.Ordinal).Equals(compactConcept, StringComparison.Ordinal))
            {
                score += 18;
            }
            else
            {
                // Length proximity rewards totalamount over totalamountlessfreight for "total amount"-like concepts.
                var proximity = Math.Abs(logical.Length - compactConcept.Length);
                score += Math.Max(0, 14 - proximity);
            }
        }

        score += TypeBoost(tokens, attr.Kind);
        return Math.Max(0, score);
    }

    private static int TypeBoost(IReadOnlyList<string> tokens, CrmAttributeKind kind)
    {
        var moneyish = tokens.Any(t => t is "amount" or "total" or "revenue" or "value" or "price" or "worth");
        var nameish = tokens.Any(t => t is "name" or "fullname" or "title");
        var dateish = tokens.Any(t => t is "date" or "year" or "month" or "created" or "recent");
        var countish = tokens.Any(t => t is "count" or "number" or "qty" or "quantity");

        return kind switch
        {
            CrmAttributeKind.Money or CrmAttributeKind.Decimal when moneyish => 22,
            CrmAttributeKind.String or CrmAttributeKind.Memo when nameish => 14,
            CrmAttributeKind.DateTime when dateish => 14,
            CrmAttributeKind.Integer when countish => 12,
            CrmAttributeKind.Lookup or CrmAttributeKind.Customer when
                tokens.Any(t => t is "customer" or "account" or "contact" or "owner") => 16,
            _ => 0
        };
    }

    private static IReadOnlyList<string> ResolveLookupTargets(CrmEntityMetadata entity, CrmAttributeMetadata attr)
    {
        if (attr.Kind is not (CrmAttributeKind.Lookup or CrmAttributeKind.Customer or CrmAttributeKind.Owner))
        {
            return Array.Empty<string>();
        }

        return entity.Relationships
            .Where(r =>
                r.IsApproved &&
                !string.IsNullOrWhiteSpace(r.ReferencingAttribute) &&
                r.ReferencingAttribute!.Equals(attr.LogicalName, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ReferencedEntity)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
    }

    private async Task<CrmSchemaRelationshipMatch?> DiscoverRelationshipsAsync(
        string leftRaw,
        string rightRaw,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        if (!TryResolveApproved(leftRaw, out var left) || !TryResolveApproved(rightRaw, out var right))
        {
            return null;
        }

        if (!_auth.AuthorizeRead(left, "read_schema_metadata").Allowed ||
            !_auth.AuthorizeRead(right, "read_schema_metadata").Allowed)
        {
            return null;
        }

        var leftMeta = await _metadata.GetEntityAsync(left, cancellationToken);
        var rightMeta = await _metadata.GetEntityAsync(right, cancellationToken);
        if (leftMeta is null || rightMeta is null)
        {
            return null;
        }

        var candidates = leftMeta.Relationships
            .Concat(rightMeta.Relationships)
            .Where(r => r.IsApproved && r.Kind != CrmRelationshipKind.ManyToMany)
            .Where(r => Relates(r, left, right))
            .Where(r => _policy.IsRelationshipApproved(r.SchemaName, r.ReferencingEntity, r.ReferencedEntity))
            .GroupBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxCandidates))
            .Select(r => new CrmSchemaRelationshipCandidate
            {
                SchemaName = r.SchemaName,
                ReferencingEntity = r.ReferencingEntity,
                ReferencingAttribute = r.ReferencingAttribute,
                ReferencedEntity = r.ReferencedEntity,
                ReferencedAttribute = r.ReferencedAttribute,
                LookupDisplayLabel = r.ReferencingAttributeDisplayName,
                BusinessLabel = r.BusinessLabel,
                Kind = r.Kind.ToString()
            })
            .ToArray();

        if (candidates.Length == 0)
        {
            return null;
        }

        return new CrmSchemaRelationshipMatch
        {
            FromEntity = left,
            ToEntity = right,
            Candidates = candidates
        };
    }

    private bool TryResolveApproved(string raw, out string logical)
    {
        logical = string.Empty;
        if (_policy.TryResolveEntityAlias(raw, out var resolved) && _policy.IsApproved(resolved))
        {
            logical = resolved;
            return true;
        }

        if (_policy.IsApproved(raw))
        {
            logical = raw.Trim();
            return true;
        }

        return false;
    }

    private static bool Relates(CrmRelationshipMetadata r, string a, string b) =>
        (r.ReferencingEntity.Equals(a, StringComparison.OrdinalIgnoreCase) &&
         r.ReferencedEntity.Equals(b, StringComparison.OrdinalIgnoreCase)) ||
        (r.ReferencingEntity.Equals(b, StringComparison.OrdinalIgnoreCase) &&
         r.ReferencedEntity.Equals(a, StringComparison.OrdinalIgnoreCase));

    private async Task<CrmSchemaFieldCandidate> ToFieldCandidateAsync(
        RankedAttribute ranked,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CrmSchemaOptionValue>? options = null;
        string? optionsLoadError = null;
        if (Enum.TryParse<CrmAttributeKind>(ranked.Type, ignoreCase: true, out var kind) &&
            DynamicsCrmMetadataService.IsChoiceAttributeKind(kind))
        {
            var load = await _metadata.EnsureAttributeOptionsAsync(
                ranked.EntityLogicalName,
                ranked.LogicalName,
                cancellationToken);
            if (load.OptionsAvailable && load.Attribute is not null && load.Attribute.OptionSetByValue.Count > 0)
            {
                options = load.Attribute.OptionSetByValue
                    .OrderBy(kv => kv.Key)
                    .Select(kv => new CrmSchemaOptionValue
                    {
                        Value = kv.Key,
                        Label = kv.Value
                    })
                    .Take(CrmSchemaDiscoveryLimits.MaxOptionsPerAttribute)
                    .ToArray();
            }
            else if (!string.IsNullOrWhiteSpace(load.Error))
            {
                optionsLoadError = load.Error;
                _logger.LogWarning(
                    "[METADATA SEARCH] OptionSet enrichment failed Entity={Entity} Attribute={Attribute} Error={Error}",
                    ranked.EntityLogicalName,
                    ranked.LogicalName,
                    Truncate(load.Error, 200));
            }
        }

        return new CrmSchemaFieldCandidate
        {
            LogicalName = ranked.LogicalName,
            DisplayName = ranked.DisplayName,
            Type = ranked.Type,
            LookupTargets = ranked.LookupTargets,
            Score = ranked.Score,
            Options = options,
            OptionsLoadError = optionsLoadError
        };
    }

    internal static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        return NonTokenRegex().Split(Normalize(text))
            .Select(t => t.Trim())
            .Where(t => t.Length >= 2)
            .Where(t => t is not ("the" or "and" or "for" or "with" or "from" or "our" or "their"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Serialize(CrmSchemaDiscoveryResult result) =>
        JsonSerializer.Serialize(
            new
            {
                fields = result.Fields.Select(f => new
                {
                    concept = f.Concept,
                    entity = f.Entity,
                    lowConfidence = f.LowConfidence ? true : (bool?)null,
                    candidates = f.Candidates.Select(c => new
                    {
                        logicalName = c.LogicalName,
                        displayName = c.DisplayName,
                        type = c.Type,
                        lookupTargets = c.LookupTargets.Count > 0 ? c.LookupTargets : null,
                        score = c.Score,
                        options = c.Options is { Count: > 0 }
                            ? c.Options.Select(o => new { value = o.Value, label = o.Label })
                            : null,
                        optionsLoadError = c.OptionsLoadError
                    })
                }),
                relationships = result.Relationships.Select(r => new
                {
                    fromEntity = r.FromEntity,
                    toEntity = r.ToEntity,
                    candidates = r.Candidates.Select(c => new
                    {
                        schemaName = c.SchemaName,
                        referencingEntity = c.ReferencingEntity,
                        referencingAttribute = c.ReferencingAttribute,
                        referencedEntity = c.ReferencedEntity,
                        referencedAttribute = c.ReferencedAttribute,
                        lookupDisplayLabel = c.LookupDisplayLabel,
                        businessLabel = c.BusinessLabel,
                        kind = c.Kind
                    })
                }),
                entities = result.Entities.Count > 0
                    ? result.Entities.Select(e => new
                    {
                        logicalName = e.LogicalName,
                        primaryIdAttribute = e.PrimaryIdAttribute,
                        primaryNameAttribute = e.PrimaryNameAttribute
                    })
                    : null,
                unresolvedConcepts = result.UnresolvedConcepts,
                notes = result.Notes.Count > 0 ? result.Notes : null,
                responseCharacters = result.ResponseCharacters > 0 ? result.ResponseCharacters : (int?)null
            },
            JsonOptions);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public sealed record ScopedEntity(CrmEntityMetadata Entity);

    public sealed record RankedAttribute(
        string EntityLogicalName,
        string LogicalName,
        string? DisplayName,
        string Type,
        IReadOnlyList<string> LookupTargets,
        int Score);

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonTokenRegex();
}
