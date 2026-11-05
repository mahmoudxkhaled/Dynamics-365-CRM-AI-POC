namespace CrmAiPoc.Api.Crm.Metadata.Semantic;

public interface IOllamaEmbeddingClient
{
    Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default);
}
