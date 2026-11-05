namespace CrmAiPoc.Api.Crm.Metadata.Semantic;

/// <summary>
/// Semantic retrieval over approved CRM metadata. Used by ChatService to select the AI catalog.
/// Backend query validation continues to use the full authorized metadata cache.
/// </summary>
public interface ISemanticCrmMetadataRetriever
{
    Task<SemanticMetadataRetrievalResult> RetrieveAsync(
        string userQuestion,
        CancellationToken cancellationToken = default);
}
