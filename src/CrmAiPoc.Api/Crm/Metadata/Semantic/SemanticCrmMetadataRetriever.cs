using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Metadata.Semantic;

public sealed class SemanticCrmMetadataRetriever : ISemanticCrmMetadataRetriever
{
    private readonly ICrmMetadataService _metadata;
    private readonly EntityApprovalPolicy _policy;
    private readonly IOllamaEmbeddingClient _embeddings;
    private readonly InMemorySemanticMetadataIndex _index;
    private readonly SemanticMetadataOptions _options;
    private readonly ILogger<SemanticCrmMetadataRetriever> _logger;

    public SemanticCrmMetadataRetriever(
        ICrmMetadataService metadata,
        EntityApprovalPolicy policy,
        IOllamaEmbeddingClient embeddings,
        InMemorySemanticMetadataIndex index,
        IOptions<SemanticMetadataOptions> options,
        ILogger<SemanticCrmMetadataRetriever> logger)
    {
        _metadata = metadata;
        _policy = policy;
        _embeddings = embeddings;
        _index = index;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SemanticMetadataRetrievalResult> RetrieveAsync(
        string userQuestion,
        CancellationToken cancellationToken = default)
    {
        var question = (userQuestion ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            return new SemanticMetadataRetrievalResult
            {
                Question = question,
                ContentHash = string.Empty,
                Error = "Question is required."
            };
        }

        try
        {
            await _metadata.EnsureApprovedEntitiesLoadedAsync(cancellationToken);
            var approved = (await _metadata.GetApprovedEntitiesAsync(cancellationToken))
                .Where(e => _policy.IsApproved(e.LogicalName))
                .ToList();

            if (approved.Count == 0)
            {
                return new SemanticMetadataRetrievalResult
                {
                    Question = question,
                    ContentHash = string.Empty,
                    Error = "No approved CRM entities are available for semantic indexing."
                };
            }

            var hash = MetadataDocumentBuilder.ComputeContentHash(approved);
            var docs = MetadataDocumentBuilder.Build(approved);
            var ensure = await _index.EnsureBuiltAsync(
                hash,
                docs,
                async (inputs, ct) => await _embeddings.EmbedBatchAsync(inputs, ct),
                cancellationToken);

            if (!ensure.Success || ensure.Snapshot is null)
            {
                return new SemanticMetadataRetrievalResult
                {
                    Question = question,
                    ContentHash = _index.ContentHash ?? string.Empty,
                    IndexedDocumentCount = _index.DocumentCount,
                    Error = ensure.FailureReason ?? "Semantic index is not ready."
                };
            }

            var snapshot = ensure.Snapshot;
            var rebuilt = ensure.Rebuilt;

            _logger.LogInformation(
                "[SEMANTIC INDEX] ContentHash={ContentHash} IndexedDocumentCount={IndexedDocumentCount} " +
                "IndexRebuilt={IndexRebuilt} IndexInitMode={IndexInitMode} State={State}",
                snapshot.ContentHash,
                snapshot.Vectors.Count,
                rebuilt,
                rebuilt ? "Built" : "Reused",
                ensure.State);

            var queryVector = await _embeddings.EmbedAsync(
                MetadataDocumentBuilder.PrefixQuery(question),
                cancellationToken);

            var scored = snapshot.Vectors
                .Select(v => new SemanticHit
                {
                    Document = v.Document,
                    Similarity = CosineSimilarity.Compute(queryVector, v.Embedding)
                })
                .Where(h => h.Similarity >= _options.MinSimilarity)
                .OrderByDescending(h => h.Similarity)
                .ToList();

            var entityHits = TakeTop(scored, MetadataSearchDocumentKind.Entity, _options.TopKEntities);
            var selectedEntities = entityHits
                .Select(h => h.Document.EntityLogicalName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var approvedByName = approved.ToDictionary(
                e => e.LogicalName,
                StringComparer.OrdinalIgnoreCase);

            var relationshipHits = SelectRelationships(scored, selectedEntities);
            var attributeHits = SelectAttributes(
                scored,
                entityHits,
                selectedEntities,
                approvedByName,
                relationshipHits);

            var estimatedChars = EstimatePayloadChars(entityHits, attributeHits, relationshipHits);

            _logger.LogInformation(
                "Semantic metadata retrieval: questionLength={QuestionLength}, indexed={Indexed}, " +
                "entities={Entities}, attributes={Attributes}, relationships={Relationships}, rebuilt={Rebuilt}",
                question.Length,
                snapshot.Vectors.Count,
                entityHits.Count,
                attributeHits.Count,
                relationshipHits.Count,
                rebuilt);

            return new SemanticMetadataRetrievalResult
            {
                Question = question,
                ContentHash = snapshot.ContentHash,
                IndexedDocumentCount = snapshot.Vectors.Count,
                RetrievedDocumentCount = entityHits.Count + attributeHits.Count + relationshipHits.Count,
                EstimatedPayloadChars = estimatedChars,
                IndexRebuilt = rebuilt,
                EntityHits = entityHits,
                AttributeHits = attributeHits,
                RelationshipHits = relationshipHits
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Semantic metadata retrieval failed.");
            return new SemanticMetadataRetrievalResult
            {
                Question = question,
                ContentHash = _index.ContentHash ?? string.Empty,
                IndexedDocumentCount = _index.DocumentCount,
                Error = ex.Message
            };
        }
    }

    private IReadOnlyList<SemanticHit> SelectAttributes(
        IReadOnlyList<SemanticHit> scored,
        IReadOnlyList<SemanticHit> entityHits,
        HashSet<string> selectedEntities,
        IReadOnlyDictionary<string, CrmEntityMetadata> approvedByName,
        IReadOnlyList<SemanticHit> relationshipHits)
    {
        if (_options.TopKAttributesPerEntity <= 0 && entityHits.Count == 0)
        {
            return Array.Empty<SemanticHit>();
        }

        var attributeHitsByEntity = scored
            .Where(h => h.Document.Kind == MetadataSearchDocumentKind.Attribute)
            .Where(h => selectedEntities.Contains(h.Document.EntityLogicalName))
            .GroupBy(h => h.Document.EntityLogicalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(h => h.Similarity).ToList(),
                StringComparer.OrdinalIgnoreCase);

        // All scored attributes (for structural endpoints that may sit on non-selected entities).
        var allAttributesByKey = scored
            .Where(h => h.Document.Kind == MetadataSearchDocumentKind.Attribute)
            .Where(h => !string.IsNullOrWhiteSpace(h.Document.AttributeLogicalName))
            .GroupBy(
                h => AttributeKey(h.Document.EntityLogicalName, h.Document.AttributeLogicalName!),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var selected = new List<SemanticHit>();
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(SemanticHit? hit)
        {
            if (hit is null || string.IsNullOrWhiteSpace(hit.Document.AttributeLogicalName))
            {
                return;
            }

            var key = AttributeKey(hit.Document.EntityLogicalName, hit.Document.AttributeLogicalName!);
            if (!selectedKeys.Add(key))
            {
                return;
            }

            selected.Add(hit);
        }

        foreach (var entityHit in entityHits)
        {
            var entityName = entityHit.Document.EntityLogicalName;
            if (attributeHitsByEntity.TryGetValue(entityName, out var ranked))
            {
                var take = Math.Max(0, _options.TopKAttributesPerEntity);
                foreach (var hit in ranked.Take(take))
                {
                    TryAdd(hit);
                }
            }

            // Structural identity + audit dates from Dynamics metadata — not similarity-gated.
            if (approvedByName.TryGetValue(entityName, out var meta))
            {
                TryAddStructuralAttribute(allAttributesByKey, entityName, meta.PrimaryIdAttribute, TryAdd);
                TryAddStructuralAttribute(allAttributesByKey, entityName, meta.PrimaryNameAttribute, TryAdd);
                TryAddStructuralAttribute(allAttributesByKey, entityName, "createdon", TryAdd);
                TryAddStructuralAttribute(allAttributesByKey, entityName, "modifiedon", TryAdd);
            }
        }

        // Relationship endpoint / lookup attributes required to interpret selected relationships.
        foreach (var rel in relationshipHits)
        {
            TryAddStructuralAttribute(
                allAttributesByKey,
                rel.Document.ReferencingEntity,
                rel.Document.ReferencingAttribute,
                TryAdd);
            TryAddStructuralAttribute(
                allAttributesByKey,
                rel.Document.ReferencedEntity,
                rel.Document.ReferencedAttribute,
                TryAdd);
        }

        return selected;
    }

    private IReadOnlyList<SemanticHit> SelectRelationships(
        IReadOnlyList<SemanticHit> scored,
        HashSet<string> selectedEntities)
    {
        if (_options.TopKRelationships <= 0 || selectedEntities.Count == 0)
        {
            return Array.Empty<SemanticHit>();
        }

        // Relevant = either endpoint (or owner entity) is in the selected entity set.
        // Deduplicate by schema name, keeping the highest similarity document.
        var bestBySchema = new Dictionary<string, SemanticHit>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in scored.Where(h => h.Document.Kind == MetadataSearchDocumentKind.Relationship))
        {
            var doc = hit.Document;
            var touchesSelected =
                selectedEntities.Contains(doc.EntityLogicalName) ||
                (!string.IsNullOrWhiteSpace(doc.ReferencingEntity) &&
                 selectedEntities.Contains(doc.ReferencingEntity)) ||
                (!string.IsNullOrWhiteSpace(doc.ReferencedEntity) &&
                 selectedEntities.Contains(doc.ReferencedEntity));

            if (!touchesSelected || string.IsNullOrWhiteSpace(doc.RelationshipSchemaName))
            {
                continue;
            }

            if (!bestBySchema.TryGetValue(doc.RelationshipSchemaName, out var existing) ||
                hit.Similarity > existing.Similarity)
            {
                bestBySchema[doc.RelationshipSchemaName] = hit;
            }
        }

        return bestBySchema.Values
            .OrderByDescending(h => h.Similarity)
            .Take(_options.TopKRelationships)
            .ToArray();
    }

    private static void TryAddStructuralAttribute(
        IReadOnlyDictionary<string, SemanticHit> allAttributesByKey,
        string? entityLogicalName,
        string? attributeLogicalName,
        Action<SemanticHit?> tryAdd)
    {
        if (string.IsNullOrWhiteSpace(entityLogicalName) ||
            string.IsNullOrWhiteSpace(attributeLogicalName))
        {
            return;
        }

        var key = AttributeKey(entityLogicalName, attributeLogicalName);
        if (allAttributesByKey.TryGetValue(key, out var hit))
        {
            tryAdd(hit);
        }
    }

    private static string AttributeKey(string entity, string attribute) =>
        entity + "." + attribute;

    private static IReadOnlyList<SemanticHit> TakeTop(
        IEnumerable<SemanticHit> scored,
        MetadataSearchDocumentKind kind,
        int topK)
    {
        if (topK <= 0)
        {
            return Array.Empty<SemanticHit>();
        }

        return scored
            .Where(h => h.Document.Kind == kind)
            .Take(topK)
            .ToArray();
    }

    private static int EstimatePayloadChars(
        IReadOnlyList<SemanticHit> entities,
        IReadOnlyList<SemanticHit> attributes,
        IReadOnlyList<SemanticHit> relationships)
    {
        // Rough catalog-fragment estimate without serializing CRM records or vectors.
        var total = 0;
        foreach (var h in entities)
        {
            total += 80 + (h.Document.SearchText?.Length ?? 0);
        }

        foreach (var h in attributes)
        {
            total += 60 + (h.Document.SearchText?.Length ?? 0);
        }

        foreach (var h in relationships)
        {
            total += 100 + (h.Document.SearchText?.Length ?? 0);
        }

        return total;
    }
}
