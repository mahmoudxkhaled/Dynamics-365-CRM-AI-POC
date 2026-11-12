namespace CrmAiPoc.Api.Crm.FetchXml;

/// <summary>
/// Validates LLM-generated FetchXML and executes it against Dynamics (read-only).
/// </summary>
public interface IFetchXmlQueryExecutor
{
    Task<FetchXmlValidationOutcome> ValidateAsync(
        FetchXmlToolRequest request,
        CancellationToken cancellationToken = default);

    Task<Generic.GenericCrmQueryResult> ExecuteAsync(
        ValidatedFetchXmlQuery query,
        CancellationToken cancellationToken = default);
}
