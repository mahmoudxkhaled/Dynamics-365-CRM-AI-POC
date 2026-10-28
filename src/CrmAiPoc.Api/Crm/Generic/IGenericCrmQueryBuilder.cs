using CrmAiPoc.Api.Crm.Metadata;

namespace CrmAiPoc.Api.Crm.Generic;

public interface IGenericCrmQueryBuilder
{
    GenericCrmODataBuildResult Build(CrmQueryPlan plan, CrmEntityMetadata entity, CrmEntityMetadata? relatedFrom = null);
}

public sealed class GenericCrmODataBuildResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? RelativeRequestUri { get; init; }
    public CrmQueryOperation Operation { get; init; }
    public IReadOnlyList<string> AppliedFilterSummary { get; init; } = Array.Empty<string>();
    public string? SortSummary { get; init; }
    public int Limit { get; init; }
    public IReadOnlyList<string> SelectFields { get; init; } = Array.Empty<string>();

    public static GenericCrmODataBuildResult Ok(
        string uri,
        CrmQueryOperation operation,
        IReadOnlyList<string> summaries,
        string? sortSummary,
        int limit,
        IReadOnlyList<string> select) =>
        new()
        {
            Success = true,
            RelativeRequestUri = uri,
            Operation = operation,
            AppliedFilterSummary = summaries,
            SortSummary = sortSummary,
            Limit = limit,
            SelectFields = select
        };

    public static GenericCrmODataBuildResult Fail(string error) =>
        new() { Success = false, Error = error };
}
