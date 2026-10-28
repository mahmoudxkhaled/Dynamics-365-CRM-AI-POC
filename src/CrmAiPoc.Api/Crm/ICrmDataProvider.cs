using CrmAiPoc.Api.Crm.Models;
using CrmAiPoc.Api.Crm.Query;

namespace CrmAiPoc.Api.Crm;

/// <summary>
/// Read-only CRM data access. Implementations must never expose create/update/delete.
/// </summary>
public interface ICrmDataProvider
{
    string ProviderName { get; }

    Task<CrmQueryResult<CrmAccount>> SearchAccountsAsync(
        string name,
        int limit,
        CancellationToken cancellationToken = default);

    Task<CrmQueryResult<CrmContact>> GetContactsForAccountAsync(
        Guid accountId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<int> CountAccountsAsync(CancellationToken cancellationToken = default);

    Task<CrmQueryResult<CrmSalesOrder>> GetSalesOrdersAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a validated structured salesorder query plan (list or count).
    /// </summary>
    Task<SalesOrderQueryExecutionResult> QuerySalesOrdersAsync(
        SalesOrderQueryPlan plan,
        CancellationToken cancellationToken = default);
}
