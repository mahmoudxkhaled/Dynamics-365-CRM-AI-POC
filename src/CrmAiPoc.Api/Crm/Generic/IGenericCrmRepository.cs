namespace CrmAiPoc.Api.Crm.Generic;

public interface IGenericCrmRepository
{
    string ProviderName { get; }

    Task<GenericCrmQueryResult> QueryAsync(CrmQueryPlan plan, CancellationToken cancellationToken = default);
}
