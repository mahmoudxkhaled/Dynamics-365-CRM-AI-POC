using System.Diagnostics;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Metadata.Discovery;

/// <summary>
/// Merges lexical attribute scores with Ollama embedding similarity for discover_crm_schema.
/// Falls back to lexical-only when hybrid is disabled or Ollama fails.
/// </summary>
public sealed class HybridMetadataRanker
{
    private readonly ICrmMetadataService _metadata;
    private readonly EntityApprovalPolicy _policy;
    private readonly IOllamaEmbeddingClient _embeddings;
    private readonly InMemorySemanticMetadataIndex _index;
    private readonly SemanticMetadataOptions _options;
    private readonly OllamaEmbeddingOptions _embeddingOptions;
    private readonly ILogger<HybridMetadataRanker> _logger;

    public HybridMetadataRanker(
        ICrmMetadataService metadata,
        EntityApprovalPolicy policy,
        IOllamaEmbeddingClient embeddings,
        InMemorySemanticMetadataIndex index,
        IOptions<SemanticMetadataOptions> options,
        IOptions<OllamaEmbeddingOptions> embeddingOptions,
        ILogger<HybridMetadataRanker> logger)
    {
        _metadata = metadata;
        _policy = policy;
        _embeddings = embeddings;
        _index = index;
        _options = options.Value;
        _embeddingOptions = embeddingOptions.Value;
        _logger = logger;
    }

    public bool HybridEnabled => _options.EnableHybridRetrieval;

    /// <summary>
    /// Builds (or loads) the semantic metadata index from approved CRM metadata.
    /// Intended for background warm-up; safe to call concurrently with chat requests.
    /// </summary>
    public async Task<EnsureIndexOutcome> WarmIndexAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.EnableHybridRetrieval)
        {
            return new EnsureIndexOutcome
            {
                Success = false,
                State = SemanticIndexState.NotStarted,
                FailureReason = "hybrid-disabled"
            };
        }

        await _metadata.EnsureApprovedEntitiesLoadedAsync(cancellationToken);
        var approved = (await _metadata.GetApprovedEntitiesAsync(cancellationToken))
            .Where(e => _policy.IsApproved(e.LogicalName))
            .ToList();
        if (approved.Count == 0)
        {
            return new EnsureIndexOutcome
            {
                Success = false,
                State = SemanticIndexState.NotStarted,
                FailureReason = "no-approved-entities"
            };
        }

        var metaHash = MetadataDocumentBuilder.ComputeContentHash(approved);
        var hash =
            $"{metaHash}|{_embeddingOptions.EmbeddingModel}|{_embeddingOptions.ExpectedDimensions}";
        var docs = MetadataDocumentBuilder.Build(approved);

        _logger.LogInformation(
            "[HYBRID METADATA] WarmIndex starting DocumentCount={Docs} Model={Model}",
            docs.Count,
            _embeddingOptions.EmbeddingModel);

        var outcome = await _index.EnsureBuiltAsync(
            hash,
            docs,
            async (inputs, ct) => await _embeddings.EmbedBatchAsync(inputs, ct),
            cancellationToken);

        _logger.LogInformation(
            "[HYBRID METADATA] WarmIndex finished Success={Success} State={State} Rebuilt={Rebuilt} " +
            "CacheHit={CacheHit} LoadedFromDisk={LoadedFromDisk} DocumentCount={Docs} DurationMs={Ms} " +
            "CacheFilePath={CacheFilePath} Reason={Reason}",
            outcome.Success,
            outcome.State,
            outcome.Rebuilt,
            outcome.CacheHit,
            outcome.LoadedFromDisk,
            outcome.DocumentCount,
            outcome.DurationMs,
            _index.CacheFilePath ?? "(disabled)",
            outcome.FailureReason ?? "-");

        return outcome;
    }

    public async Task<HybridRankResult> RankAttributesAsync(
        string concept,
        IReadOnlyList<CrmSchemaDiscoveryService.ScopedEntity> scope,
        IReadOnlyList<CrmSchemaDiscoveryService.RankedAttribute> lexicalRanked,
        int maxCandidates,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var lexicalWeight = Clamp01(_options.LexicalWeight);
        var semanticWeight = Clamp01(_options.SemanticWeight);
        var weightSum = lexicalWeight + semanticWeight;
        if (weightSum <= 0)
        {
            lexicalWeight = 0.55;
            semanticWeight = 0.45;
            weightSum = 1.0;
        }

        lexicalWeight /= weightSum;
        semanticWeight /= weightSum;

        if (!_options.EnableHybridRetrieval)
        {
            return LexicalOnly(lexicalRanked, maxCandidates, sw, "hybrid-disabled");
        }

        try
        {
            await _metadata.EnsureApprovedEntitiesLoadedAsync(cancellationToken);
            var approved = (await _metadata.GetApprovedEntitiesAsync(cancellationToken))
                .Where(e => _policy.IsApproved(e.LogicalName))
                .ToList();
            if (approved.Count == 0)
            {
                return LexicalOnly(lexicalRanked, maxCandidates, sw, "no-approved-entities");
            }

            // Invalidate when metadata OR embedding model/dimensions change.
            var metaHash = MetadataDocumentBuilder.ComputeContentHash(approved);
            var hash =
                $"{metaHash}|{_embeddingOptions.EmbeddingModel}|{_embeddingOptions.ExpectedDimensions}";
            var docs = MetadataDocumentBuilder.Build(approved);

            if (_index.TryGetReadySnapshot(hash, out var readySnapshot))
            {
                return await RankWithSnapshotAsync(
                    concept,
                    scope,
                    lexicalRanked,
                    maxCandidates,
                    readySnapshot,
                    rebuilt: false,
                    lexicalWeight,
                    semanticWeight,
                    correlationId,
                    sw,
                    cancellationToken);
            }

            if (_index.IsInFailureCooldown(hash))
            {
                _logger.LogInformation(
                    "[HYBRID METADATA] CorrelationId={CorrelationId} Mode=lexical-fallback Concept={Concept} " +
                    "Reason=index-failure-cooldown IndexState={State}",
                    correlationId ?? "-",
                    Truncate(concept, 80),
                    _index.State);
                return LexicalOnly(
                    lexicalRanked,
                    maxCandidates,
                    sw,
                    Truncate(_index.LastFailureReason ?? "index-failure-cooldown", 160));
            }

            var ensure = await _index.EnsureBuiltAsync(
                hash,
                docs,
                async (inputs, ct) => await _embeddings.EmbedBatchAsync(inputs, ct),
                cancellationToken);

            if (!ensure.Success || ensure.Snapshot is null)
            {
                var reason = ensure.SkippedDueToFailureCooldown
                    ? "index-failure-cooldown"
                    : (ensure.FailureReason ?? "index-not-ready");
                _logger.LogWarning(
                    "[HYBRID METADATA] CorrelationId={CorrelationId} Mode=lexical-fallback Concept={Concept} " +
                    "Reason={Reason} IndexState={State} DocumentCount={Docs} CompletedBatches={Completed}/{Batches}",
                    correlationId ?? "-",
                    Truncate(concept, 80),
                    reason,
                    ensure.State,
                    ensure.DocumentCount,
                    ensure.CompletedBatches,
                    ensure.BatchCount);
                return LexicalOnly(lexicalRanked, maxCandidates, sw, Truncate(reason, 160));
            }

            return await RankWithSnapshotAsync(
                concept,
                scope,
                lexicalRanked,
                maxCandidates,
                ensure.Snapshot,
                rebuilt: ensure.Rebuilt,
                lexicalWeight,
                semanticWeight,
                correlationId,
                sw,
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested && ex is OperationCanceledException)
            {
                throw;
            }

            _logger.LogWarning(
                ex,
                "[HYBRID METADATA] CorrelationId={CorrelationId} Mode=lexical-fallback Concept={Concept} Reason={Reason}",
                correlationId ?? "-",
                Truncate(concept, 80),
                ex.Message);

            return LexicalOnly(lexicalRanked, maxCandidates, sw, Truncate(ex.Message, 160));
        }
    }

    private async Task<HybridRankResult> RankWithSnapshotAsync(
        string concept,
        IReadOnlyList<CrmSchemaDiscoveryService.ScopedEntity> scope,
        IReadOnlyList<CrmSchemaDiscoveryService.RankedAttribute> lexicalRanked,
        int maxCandidates,
        InMemorySemanticMetadataIndex.IndexSnapshot snapshot,
        bool rebuilt,
        double lexicalWeight,
        double semanticWeight,
        string? correlationId,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        try
        {
            var focusedConcept = FocusConceptAwayFromEntityNames(concept, scope);
            var queryVector = await _embeddings.EmbedAsync(
                MetadataDocumentBuilder.PrefixQuery(focusedConcept),
                cancellationToken);

            var scopeNames = scope
                .Select(s => s.Entity.LogicalName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var semanticHits = snapshot.Vectors
                .Where(v => v.Document.Kind == MetadataSearchDocumentKind.Attribute)
                .Where(v => scopeNames.Count == 0 || scopeNames.Contains(v.Document.EntityLogicalName))
                .Where(v => !string.IsNullOrWhiteSpace(v.Document.AttributeLogicalName))
                .Select(v => new
                {
                    v.Document.EntityLogicalName,
                    AttributeLogicalName = v.Document.AttributeLogicalName!,
                    v.Document.DisplayName,
                    Similarity = CosineSimilarity.Compute(queryVector, v.Embedding)
                })
                .Where(h => h.Similarity >= Math.Min(_options.MinSemanticSimilarity, 0.01f))
                .OrderByDescending(h => h.Similarity)
                .Take(Math.Max(5, _options.SemanticCandidatePoolSize))
                .ToList();

            var lexicalByKey = lexicalRanked.ToDictionary(
                r => AttributeKey(r.EntityLogicalName, r.LogicalName),
                r => r,
                StringComparer.OrdinalIgnoreCase);

            var semanticByKey = semanticHits.ToDictionary(
                h => AttributeKey(h.EntityLogicalName, h.AttributeLogicalName),
                h => h,
                StringComparer.OrdinalIgnoreCase);

            var keys = lexicalByKey.Keys
                .Concat(semanticByKey.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            var attrLookup = scope
                .SelectMany(s => s.Entity.Attributes
                    .Where(a => a.IsValidForRead)
                    .Select(a => (Entity: s.Entity, Attr: a)))
                .GroupBy(
                    x => AttributeKey(x.Entity.LogicalName, x.Attr.LogicalName),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var merged = new List<CrmSchemaDiscoveryService.RankedAttribute>();
            foreach (var key in keys)
            {
                lexicalByKey.TryGetValue(key, out var lex);
                semanticByKey.TryGetValue(key, out var sem);

                var lexicalScore = lex?.Score ?? 0;
                var semanticSim = sem?.Similarity ?? 0f;

                // Strong exact/display lexical matches stay authoritative.
                if (lexicalScore >= 90)
                {
                    merged.Add(lex!);
                    continue;
                }

                if (lex is null && semanticSim < _options.MinSemanticSimilarity)
                {
                    continue;
                }

                if (lex is null && !attrLookup.ContainsKey(key))
                {
                    continue;
                }

                var lexicalNorm = Math.Min(1.0, lexicalScore / 100.0);
                var hybrid = (lexicalWeight * lexicalNorm) + (semanticWeight * semanticSim);
                var hybridScore = (int)Math.Round(hybrid * 100.0, MidpointRounding.AwayFromZero);

                // Preserve lexical floor so strong lexical near-matches are not erased.
                hybridScore = Math.Max(hybridScore, lexicalScore > 0 ? Math.Min(lexicalScore, hybridScore + 15) : hybridScore);

                if (lex is null && hybridScore < CrmSchemaDiscoveryLimits.MinAcceptScore)
                {
                    continue;
                }

                if (lex is not null)
                {
                    merged.Add(lex with { Score = Math.Max(lex.Score, hybridScore) });
                }
                else
                {
                    var meta = attrLookup[key];
                    merged.Add(new CrmSchemaDiscoveryService.RankedAttribute(
                        meta.Entity.LogicalName,
                        meta.Attr.LogicalName,
                        meta.Attr.DisplayName,
                        meta.Attr.Kind.ToString(),
                        ResolveLookupTargets(meta.Entity, meta.Attr),
                        hybridScore));
                }
            }

            var final = merged
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.LogicalName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.EntityLogicalName, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maxCandidates))
                .ToArray();

            sw.Stop();
            var topDiag = string.Join(
                "; ",
                final.Take(5).Select(c =>
                {
                    var key = AttributeKey(c.EntityLogicalName, c.LogicalName);
                    lexicalByKey.TryGetValue(key, out var lex);
                    semanticByKey.TryGetValue(key, out var sem);
                    return $"{c.EntityLogicalName}.{c.LogicalName} final={c.Score} lex={lex?.Score ?? 0} sem={(sem?.Similarity ?? 0f):F3}";
                }));

            _logger.LogInformation(
                "[HYBRID METADATA] CorrelationId={CorrelationId} Mode=hybrid Concept={Concept} FocusedConcept={Focused} " +
                "ScopeEntities={Scope} LexicalCandidates={Lexical} SemanticCandidates={Semantic} HybridCandidates={Hybrid} " +
                "FinalSelected={Final} IndexRebuilt={Rebuilt} RebuildReason={RebuildReason} CacheHit={CacheHit} DurationMs={DurationMs} " +
                "LexicalWeight={LexicalWeight} SemanticWeight={SemanticWeight} Top=[{Top}]",
                correlationId ?? "-",
                Truncate(concept, 80),
                Truncate(focusedConcept, 80),
                scopeNames.Count,
                lexicalRanked.Count,
                semanticHits.Count,
                merged.Count,
                final.Length,
                rebuilt,
                rebuilt ? "metadata-or-model-changed" : "reuse-cached-index",
                !rebuilt,
                sw.ElapsedMilliseconds,
                lexicalWeight,
                semanticWeight,
                topDiag);

            return new HybridRankResult
            {
                Mode = "hybrid",
                Candidates = final,
                LexicalCandidateCount = lexicalRanked.Count,
                SemanticCandidateCount = semanticHits.Count,
                HybridCandidateCount = merged.Count,
                DurationMs = sw.ElapsedMilliseconds,
                IndexRebuilt = rebuilt,
                FallbackReason = null
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested && ex is OperationCanceledException)
            {
                throw;
            }

            _logger.LogWarning(
                ex,
                "[HYBRID METADATA] CorrelationId={CorrelationId} Mode=lexical-fallback Concept={Concept} Reason={Reason}",
                correlationId ?? "-",
                Truncate(concept, 80),
                ex.Message);

            return LexicalOnly(lexicalRanked, maxCandidates, sw, Truncate(ex.Message, 160));
        }
    }

    private static HybridRankResult LexicalOnly(
        IReadOnlyList<CrmSchemaDiscoveryService.RankedAttribute> lexicalRanked,
        int maxCandidates,
        Stopwatch sw,
        string reason)
    {
        sw.Stop();
        return new HybridRankResult
        {
            Mode = "lexical",
            Candidates = lexicalRanked.Take(Math.Max(1, maxCandidates)).ToArray(),
            LexicalCandidateCount = lexicalRanked.Count,
            SemanticCandidateCount = 0,
            HybridCandidateCount = lexicalRanked.Count,
            DurationMs = sw.ElapsedMilliseconds,
            IndexRebuilt = false,
            FallbackReason = reason
        };
    }

    /// <summary>
    /// Removes known entity tokens from a concept so attribute meaning is not drowned by entity names.
    /// Uses live approved entity logical/display names only (no hardcoded entity lists).
    /// </summary>
    internal static string FocusConceptAwayFromEntityNames(
        string concept,
        IReadOnlyList<CrmSchemaDiscoveryService.ScopedEntity> scope)
    {
        var tokens = CrmSchemaDiscoveryService.Tokenize(concept);
        if (tokens.Count == 0)
        {
            return concept.Trim();
        }

        var entityTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scoped in scope)
        {
            foreach (var t in CrmSchemaDiscoveryService.Tokenize(scoped.Entity.LogicalName))
            {
                entityTokens.Add(t);
            }

            foreach (var t in CrmSchemaDiscoveryService.Tokenize(scoped.Entity.DisplayName))
            {
                entityTokens.Add(t);
            }
        }

        var focused = tokens.Where(t => !entityTokens.Contains(t)).ToArray();
        return focused.Length == 0 ? string.Join(' ', tokens) : string.Join(' ', focused);
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

    private static string AttributeKey(string entity, string attribute) =>
        entity.Trim() + "|" + attribute.Trim();

    private static double Clamp01(double value) =>
        value < 0 ? 0 : value > 1 ? 1 : value;

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value ?? string.Empty;
        }

        return value[..max];
    }
}

public sealed class HybridRankResult
{
    public required string Mode { get; init; }
    public required IReadOnlyList<CrmSchemaDiscoveryService.RankedAttribute> Candidates { get; init; }
    public int LexicalCandidateCount { get; init; }
    public int SemanticCandidateCount { get; init; }
    public int HybridCandidateCount { get; init; }
    public long DurationMs { get; init; }
    public bool IndexRebuilt { get; init; }
    public string? FallbackReason { get; init; }
}
