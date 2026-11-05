using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using CrmAiPoc.Api.Models;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Endpoints;

public static class SemanticMetadataEndpoints
{
    public static RouteGroupBuilder MapSemanticMetadataEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/diagnostics").WithTags("SemanticMetadataDiagnostics");

        group.MapPost("/semantic-metadata", async (
            SemanticMetadataSearchRequestDto request,
            ISemanticCrmMetadataRetriever retriever,
            IOptions<SemanticMetadataOptions> options,
            IHostEnvironment env,
            CancellationToken cancellationToken) =>
        {
            if (!env.IsDevelopment() || !options.Value.EnableDiagnosticEndpoint)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Question))
            {
                return Results.BadRequest(new { error = "Question is required." });
            }

            var result = await retriever.RetrieveAsync(request.Question.Trim(), cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                return Results.Json(
                    new
                    {
                        error = result.Error,
                        indexedDocumentCount = result.IndexedDocumentCount,
                        contentHash = result.ContentHash
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // Never return embedding vectors or CRM business records.
            return Results.Ok(new
            {
                question = result.Question,
                contentHash = result.ContentHash,
                indexRebuilt = result.IndexRebuilt,
                indexedDocumentCount = result.IndexedDocumentCount,
                retrievedDocumentCount = result.RetrievedDocumentCount,
                estimatedPayloadChars = result.EstimatedPayloadChars,
                entities = result.EntityHits.Select(MapHit),
                attributes = result.AttributeHits.Select(MapHit),
                relationships = result.RelationshipHits.Select(MapHit)
            });
        })
        .WithName("SemanticMetadataSearchDiagnostic")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status503ServiceUnavailable)
        .Produces(StatusCodes.Status404NotFound);

        return group;
    }

    private static object MapHit(SemanticHit hit) => new
    {
        id = hit.Document.Id,
        kind = hit.Document.Kind.ToString(),
        similarity = Math.Round(hit.Similarity, 4),
        entityLogicalName = hit.Document.EntityLogicalName,
        attributeLogicalName = hit.Document.AttributeLogicalName,
        relationshipSchemaName = hit.Document.RelationshipSchemaName,
        displayName = hit.Document.DisplayName,
        attributeType = hit.Document.AttributeType,
        referencingEntity = hit.Document.ReferencingEntity,
        referencedEntity = hit.Document.ReferencedEntity,
        referencingAttribute = hit.Document.ReferencingAttribute,
        referencedAttribute = hit.Document.ReferencedAttribute,
        searchText = hit.Document.SearchText
    };
}
