using CrmAiPoc.Api.Crm.Models;
using CrmAiPoc.Api.Crm.Query;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Mock;

public sealed class MockCrmDataProvider : ICrmDataProvider
{
    private readonly IReadOnlyList<CrmAccount> _accounts;
    private readonly IReadOnlyList<CrmContact> _contacts;
    private readonly IReadOnlyList<CrmSalesOrder> _orders;
    private readonly SalesOrderODataQueryBuilder _queryBuilder;
    private readonly ILogger<MockCrmDataProvider> _logger;

    public MockCrmDataProvider(ILogger<MockCrmDataProvider> logger, IOptions<CrmOptions>? crmOptions = null)
    {
        _logger = logger;
        _accounts = MockCrmSeedData.CreateAccounts();
        _contacts = MockCrmSeedData.CreateContacts();
        _orders = MockCrmSeedData.CreateSalesOrders();
        _queryBuilder = new SalesOrderODataQueryBuilder(crmOptions);
    }

    public string ProviderName => "Mock";

    public Task<CrmQueryResult<CrmAccount>> SearchAccountsAsync(
        string name,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var matched = _accounts
            .Where(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name)
            .ToList();

        var page = matched.Take(limit).ToList();

        _logger.LogInformation(
            "Mock CRM search_accounts: name length={NameLength}, matched={Matched}, returned={Returned}",
            name.Length,
            matched.Count,
            page.Count);

        return Task.FromResult(new CrmQueryResult<CrmAccount>
        {
            Items = page,
            TotalMatched = matched.Count,
            Message = matched.Count == 0
                ? "No accounts matched the search criteria."
                : null
        });
    }

    public Task<CrmQueryResult<CrmContact>> GetContactsForAccountAsync(
        Guid accountId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var accountExists = _accounts.Any(a => a.AccountId == accountId);
        if (!accountExists)
        {
            return Task.FromResult(CrmQueryResult<CrmContact>.Empty(
                "No account exists with the specified accountId."));
        }

        var matched = _contacts
            .Where(c => c.AccountId == accountId)
            .OrderBy(c => c.FullName)
            .ToList();

        var page = matched.Take(limit).ToList();

        _logger.LogInformation(
            "Mock CRM get_contacts_for_account: accountId={AccountId}, matched={Matched}, returned={Returned}",
            accountId,
            matched.Count,
            page.Count);

        return Task.FromResult(new CrmQueryResult<CrmContact>
        {
            Items = page,
            TotalMatched = matched.Count,
            Message = matched.Count == 0
                ? "No contacts found for this account."
                : null
        });
    }

    public Task<int> CountAccountsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Count only non-confidential accounts for a realistic readable total.
        var count = _accounts.Count(a => !a.IsConfidential);
        _logger.LogInformation("Mock CRM count_accounts: count={Count}", count);
        return Task.FromResult(count);
    }

    public Task<CrmQueryResult<CrmSalesOrder>> GetSalesOrdersAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var orders = _orders
            .OrderByDescending(o => o.CreatedOn)
            .Take(limit)
            .ToList();

        _logger.LogInformation("Mock CRM get_sales_orders: returned={Returned}", orders.Count);

        return Task.FromResult(new CrmQueryResult<CrmSalesOrder>
        {
            Items = orders,
            TotalMatched = orders.Count,
            Message = orders.Count == 0 ? "No sales orders returned." : null
        });
    }

    public Task<SalesOrderQueryExecutionResult> QuerySalesOrdersAsync(
        SalesOrderQueryPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var built = _queryBuilder.Build(plan);
        if (!built.Success || string.IsNullOrWhiteSpace(built.RelativeRequestUri))
        {
            return Task.FromResult(new SalesOrderQueryExecutionResult
            {
                Operation = plan.Operation,
                RelativeRequestUri = string.Empty,
                AppliedFilterSummary = [],
                Message = built.Error ?? "Invalid sales order query plan.",
                ProviderName = ProviderName
            });
        }

        var matched = ApplyMockFilters(_orders, plan).ToList();

        if (built.Operation == SalesOrderQueryOperation.Count)
        {
            return Task.FromResult(new SalesOrderQueryExecutionResult
            {
                Operation = SalesOrderQueryOperation.Count,
                RelativeRequestUri = built.RelativeRequestUri,
                AppliedFilterSummary = built.AppliedFilterSummary,
                SortSummary = built.SortSummary,
                Limit = built.Limit,
                Count = matched.Count,
                CountMayBeCapped = false,
                ProviderName = ProviderName
            });
        }

        var sorted = ApplyMockSort(matched, plan).Take(built.Limit).ToList();
        _logger.LogInformation("Mock CRM query_sales_orders: matched={Matched}, returned={Returned}", matched.Count, sorted.Count);

        return Task.FromResult(new SalesOrderQueryExecutionResult
        {
            Operation = SalesOrderQueryOperation.List,
            RelativeRequestUri = built.RelativeRequestUri,
            AppliedFilterSummary = built.AppliedFilterSummary,
            SortSummary = built.SortSummary,
            Limit = built.Limit,
            Items = sorted,
            Message = sorted.Count == 0 ? "No sales orders matched the query." : null,
            ProviderName = ProviderName
        });
    }

    public CrmAccount? FindAccount(Guid accountId) =>
        _accounts.FirstOrDefault(a => a.AccountId == accountId);

    private static IEnumerable<CrmSalesOrder> ApplyMockFilters(
        IEnumerable<CrmSalesOrder> source,
        SalesOrderQueryPlan plan)
    {
        var query = source;

        if (plan.RelativeDate is not null)
        {
            var tz = BusinessTimeZone.Resolve();
            var (start, end, _) = RelativeDateResolver.Resolve(plan.RelativeDate.Token, DateTimeOffset.UtcNow, tz);
            query = plan.RelativeDate.Field.Equals("modifiedon", StringComparison.OrdinalIgnoreCase)
                ? query.Where(o => o.ModifiedOn >= start && o.ModifiedOn < end)
                : query.Where(o => o.CreatedOn >= start && o.CreatedOn < end);
        }

        foreach (var filter in plan.Filters)
        {
            query = ApplyOneFilter(query, filter);
        }

        return query;
    }

    private static IEnumerable<CrmSalesOrder> ApplyOneFilter(
        IEnumerable<CrmSalesOrder> source,
        SalesOrderQueryFilter filter)
    {
        if (!SalesOrderFieldCatalog.TryResolveField(filter.Field, out var field))
        {
            return source;
        }

        return field.LogicalName.ToLowerInvariant() switch
        {
            "name" => filter.Operator == SalesOrderFilterOperator.Contains
                ? source.Where(o => (o.Name ?? string.Empty).Contains(filter.Value, StringComparison.OrdinalIgnoreCase))
                : source.Where(o => string.Equals(o.Name, filter.Value, StringComparison.OrdinalIgnoreCase) ==
                                    (filter.Operator == SalesOrderFilterOperator.Eq)),
            "ordernumber" => filter.Operator == SalesOrderFilterOperator.Contains
                ? source.Where(o => (o.OrderNumber ?? string.Empty).Contains(filter.Value, StringComparison.OrdinalIgnoreCase))
                : source.Where(o => string.Equals(o.OrderNumber, filter.Value, StringComparison.OrdinalIgnoreCase) ==
                                    (filter.Operator == SalesOrderFilterOperator.Eq)),
            "totalamount" when decimal.TryParse(filter.Value, out var amount) => filter.Operator switch
            {
                SalesOrderFilterOperator.Gt => source.Where(o => (o.TotalAmount ?? 0) > amount),
                SalesOrderFilterOperator.Ge => source.Where(o => (o.TotalAmount ?? 0) >= amount),
                SalesOrderFilterOperator.Lt => source.Where(o => (o.TotalAmount ?? 0) < amount),
                SalesOrderFilterOperator.Le => source.Where(o => (o.TotalAmount ?? 0) <= amount),
                SalesOrderFilterOperator.Eq => source.Where(o => (o.TotalAmount ?? 0) == amount),
                SalesOrderFilterOperator.Ne => source.Where(o => (o.TotalAmount ?? 0) != amount),
                _ => source
            },
            "statecode" when SalesOrderFieldCatalog.TryResolveStateCode(filter.Value, out var state) =>
                filter.Operator == SalesOrderFilterOperator.Ne
                    ? source.Where(o => o.StateCode != state)
                    : source.Where(o => o.StateCode == state),
            "statuscode" when SalesOrderFieldCatalog.TryResolveStatusCode(filter.Value, out var status) =>
                filter.Operator == SalesOrderFilterOperator.Ne
                    ? source.Where(o => o.StatusCode != status)
                    : source.Where(o => o.StatusCode == status),
            _ => source
        };
    }

    private static IEnumerable<CrmSalesOrder> ApplyMockSort(
        IEnumerable<CrmSalesOrder> source,
        SalesOrderQueryPlan plan)
    {
        var field = string.IsNullOrWhiteSpace(plan.SortField) ? "createdon" : plan.SortField;
        var desc = plan.SortDirection != SalesOrderSortDirection.Asc;

        if (field.Equals("totalamount", StringComparison.OrdinalIgnoreCase))
        {
            return desc
                ? source.OrderByDescending(o => o.TotalAmount ?? 0)
                : source.OrderBy(o => o.TotalAmount ?? 0);
        }

        return desc
            ? source.OrderByDescending(o => o.CreatedOn)
            : source.OrderBy(o => o.CreatedOn);
    }
}
