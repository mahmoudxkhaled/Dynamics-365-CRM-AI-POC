using System.Text.Json;
using CrmAiPoc.Api.Crm.Models;
using CrmAiPoc.Api.Crm.Query;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Options;
using CrmAiPoc.Api.Configuration;

namespace CrmAiPoc.Api.Crm.Dynamics;

/// <summary>
/// Real Dynamics 365 On-Premises Web API provider (read-only).
/// Queries are server-built from allowlisted entities/fields only.
/// </summary>
public sealed class Dynamics365CrmProvider : ICrmDataProvider
{
    private readonly DynamicsCrmHttp _http;
    private readonly SalesOrderODataQueryBuilder _queryBuilder;
    private readonly ILogger<Dynamics365CrmProvider> _logger;
    private string _salesOrderEntitySetName = SalesOrderFieldCatalog.EntitySetName;
    private string _salesOrderPrimaryId = SalesOrderFieldCatalog.PrimaryId;
    private string _salesOrderPrimaryName = SalesOrderFieldCatalog.PrimaryName;
    private bool _schemaApplied;

    public Dynamics365CrmProvider(
        DynamicsCrmHttp http,
        IOptions<CrmOptions> crmOptions,
        ILogger<Dynamics365CrmProvider> logger)
    {
        _http = http;
        _queryBuilder = new SalesOrderODataQueryBuilder(crmOptions);
        _logger = logger;
    }

    public string ProviderName => "Dynamics365";

    public void ApplyDiscoveredSalesOrderSchema(
        string entitySetName,
        string primaryIdAttribute,
        string primaryNameAttribute,
        IEnumerable<string> selectFields)
    {
        if (!string.IsNullOrWhiteSpace(entitySetName))
        {
            _salesOrderEntitySetName = entitySetName;
        }

        if (!string.IsNullOrWhiteSpace(primaryIdAttribute))
        {
            _salesOrderPrimaryId = primaryIdAttribute;
        }

        if (!string.IsNullOrWhiteSpace(primaryNameAttribute))
        {
            _salesOrderPrimaryName = primaryNameAttribute;
        }

        CrmEntityAllowlist.ReplaceSalesOrderFields(selectFields);
        _schemaApplied = true;
        _logger.LogInformation(
            "Applied discovered salesorder schema. EntitySetName={EntitySetName}, PrimaryId={PrimaryId}, PrimaryName={PrimaryName}",
            _salesOrderEntitySetName,
            _salesOrderPrimaryId,
            _salesOrderPrimaryName);
    }

    public async Task<CrmQueryResult<CrmAccount>> SearchAccountsAsync(
        string name,
        int limit,
        CancellationToken cancellationToken = default)
    {
        _http.EnsureLiveConfigured();
        if (!CrmEntityAllowlist.IsEntityAllowed(CrmEntityAllowlist.Account))
        {
            return CrmQueryResult<CrmAccount>.Empty("account is not allowlisted.");
        }

        var escaped = EscapeODataString(name);
        var select = string.Join(",", CrmEntityAllowlist.GetAllowedFields(CrmEntityAllowlist.Account));
        var path =
            $"accounts?$select={select}&$filter=contains(name,'{escaped}')&$orderby=name&$top={limit}";

        var http = await _http.GetAsync(path, cancellationToken);
        EnsureSuccess(http);
        var accounts = ParseAccounts(http.Body);
        return new CrmQueryResult<CrmAccount>
        {
            Items = accounts,
            TotalMatched = accounts.Count,
            Message = accounts.Count == 0 ? "No accounts matched the search criteria." : null
        };
    }

    public async Task<CrmQueryResult<CrmContact>> GetContactsForAccountAsync(
        Guid accountId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        _http.EnsureLiveConfigured();
        if (!CrmEntityAllowlist.IsEntityAllowed(CrmEntityAllowlist.Contact))
        {
            return CrmQueryResult<CrmContact>.Empty("contact is not allowlisted.");
        }

        var select = string.Join(",", CrmEntityAllowlist.GetAllowedFields(CrmEntityAllowlist.Contact));
        var path =
            $"contacts?$select={select}&$filter=_parentcustomerid_value eq {accountId:D}&$orderby=fullname&$top={limit}";

        var http = await _http.GetAsync(path, cancellationToken);
        EnsureSuccess(http);
        var contacts = ParseContacts(http.Body, accountId);
        return new CrmQueryResult<CrmContact>
        {
            Items = contacts,
            TotalMatched = contacts.Count,
            Message = contacts.Count == 0 ? "No contacts found for this account." : null
        };
    }

    public async Task<int> CountAccountsAsync(CancellationToken cancellationToken = default)
    {
        _http.EnsureLiveConfigured();
        var path = "accounts?$select=accountid&$count=true&$top=1";
        var http = await _http.GetAsync(path, cancellationToken);
        EnsureSuccess(http);

        using var doc = JsonDocument.Parse(http.Body);
        if (doc.RootElement.TryGetProperty("@odata.count", out var countProp) &&
            countProp.TryGetInt32(out var count))
        {
            return count;
        }

        return 0;
    }

    public async Task<CrmQueryResult<CrmSalesOrder>> GetSalesOrdersAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        _http.EnsureLiveConfigured();

        if (!CrmEntityAllowlist.IsEntityAllowed(CrmEntityAllowlist.SalesOrder))
        {
            return CrmQueryResult<CrmSalesOrder>.Empty("salesorder is not allowlisted.");
        }

        // Entity set name must come from discovery when available; default is standard Web API plural.
        var fields = CrmEntityAllowlist.GetAllowedFields(CrmEntityAllowlist.SalesOrder)
            .Where(f => !f.StartsWith('_') || f.EndsWith("_value", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Prefer primary id/name always.
        var selectSet = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase)
        {
            _salesOrderPrimaryId,
            _salesOrderPrimaryName
        };

        var select = string.Join(",", selectSet);
        var path = $"{_salesOrderEntitySetName}?$select={select}&$orderby=createdon desc&$top={limit}";

        _logger.LogInformation(
            "get_sales_orders query prepared. EntitySet={EntitySet}, FieldCount={FieldCount}, SchemaApplied={SchemaApplied}, Limit={Limit}",
            _salesOrderEntitySetName,
            selectSet.Count,
            _schemaApplied,
            limit);

        var http = await _http.GetAsync(path, cancellationToken);
        EnsureSuccess(http);
        var orders = ParseSalesOrders(http.Body);

        return new CrmQueryResult<CrmSalesOrder>
        {
            Items = orders,
            TotalMatched = orders.Count,
            Message = orders.Count == 0 ? "No sales orders returned." : null
        };
    }

    public async Task<SalesOrderQueryExecutionResult> QuerySalesOrdersAsync(
        SalesOrderQueryPlan plan,
        CancellationToken cancellationToken = default)
    {
        _http.EnsureLiveConfigured();

        if (!CrmEntityAllowlist.IsEntityAllowed(CrmEntityAllowlist.SalesOrder))
        {
            return new SalesOrderQueryExecutionResult
            {
                Operation = plan.Operation,
                RelativeRequestUri = string.Empty,
                AppliedFilterSummary = [],
                Message = "salesorder is not allowlisted.",
                ProviderName = ProviderName
            };
        }

        var built = _queryBuilder.Build(plan);
        if (!built.Success || string.IsNullOrWhiteSpace(built.RelativeRequestUri))
        {
            return new SalesOrderQueryExecutionResult
            {
                Operation = plan.Operation,
                RelativeRequestUri = string.Empty,
                AppliedFilterSummary = [],
                Message = built.Error ?? "Invalid sales order query plan.",
                ProviderName = ProviderName
            };
        }

        // Ensure entity set name from discovery is used when available.
        var relativeUri = built.RelativeRequestUri;
        if (!string.Equals(_salesOrderEntitySetName, SalesOrderFieldCatalog.EntitySetName, StringComparison.OrdinalIgnoreCase) &&
            relativeUri.StartsWith(SalesOrderFieldCatalog.EntitySetName, StringComparison.OrdinalIgnoreCase))
        {
            relativeUri = _salesOrderEntitySetName + relativeUri[SalesOrderFieldCatalog.EntitySetName.Length..];
        }

        _logger.LogInformation(
            "query_sales_orders prepared. Operation={Operation}, Limit={Limit}, FilterCount={FilterCount}, SchemaApplied={SchemaApplied}",
            built.Operation,
            built.Limit,
            built.AppliedFilterSummary.Count,
            _schemaApplied);

        var http = await _http.GetAsync(relativeUri, cancellationToken);
        EnsureSuccess(http);

        if (built.Operation == SalesOrderQueryOperation.Count)
        {
            var count = ReadODataCount(http.Body);
            var capped = count >= SalesOrderQueryLimits.DynamicsODataCountCeiling;
            return new SalesOrderQueryExecutionResult
            {
                Operation = SalesOrderQueryOperation.Count,
                RelativeRequestUri = relativeUri,
                AppliedFilterSummary = built.AppliedFilterSummary,
                SortSummary = built.SortSummary,
                Limit = built.Limit,
                Count = count,
                CountMayBeCapped = capped,
                Message = capped
                    ? $"CRM @odata.count returned {count}, which matches the Dynamics count ceiling; the true total may be higher."
                    : null,
                ProviderName = ProviderName
            };
        }

        var orders = ParseSalesOrders(http.Body);
        return new SalesOrderQueryExecutionResult
        {
            Operation = SalesOrderQueryOperation.List,
            RelativeRequestUri = relativeUri,
            AppliedFilterSummary = built.AppliedFilterSummary,
            SortSummary = built.SortSummary,
            Limit = built.Limit,
            Items = orders,
            Message = orders.Count == 0 ? "No sales orders matched the query." : null,
            ProviderName = ProviderName
        };
    }

    private static int ReadODataCount(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("@odata.count", out var countProp) &&
            countProp.TryGetInt32(out var count))
        {
            return count;
        }

        return 0;
    }

    private static void EnsureSuccess(DynamicsHttpResult http)
    {
        if (http.IsSuccess)
        {
            return;
        }

        throw new HttpRequestException(
            $"Dynamics Web API GET failed. Status={http.StatusCode}, Category={http.ErrorCategory}, Path={http.Path}. " +
            (http.ExceptionMessage ?? "See server logs (no credentials logged)."));
    }

    private static string EscapeODataString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private List<CrmSalesOrder> ParseSalesOrders(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var values))
        {
            return [];
        }

        var list = new List<CrmSalesOrder>();
        foreach (var item in values.EnumerateArray())
        {
            var id = GetGuid(item, _salesOrderPrimaryId) ?? GetGuid(item, "salesorderid") ?? Guid.Empty;
            if (id == Guid.Empty)
            {
                continue;
            }

            var statusCode = GetInt(item, "statuscode");
            var stateCode = GetInt(item, "statecode");
            list.Add(new CrmSalesOrder
            {
                SalesOrderId = id,
                Name = GetString(item, _salesOrderPrimaryName) ?? GetString(item, "name"),
                OrderNumber = GetString(item, "ordernumber"),
                CustomerAccountId = GetGuid(item, "_te_customeraccountid_value")
                                    ?? GetGuid(item, "_customerid_value"),
                CreatedOn = GetDate(item, "createdon"),
                ModifiedOn = GetDate(item, "modifiedon"),
                TotalAmount = GetDecimal(item, "totalamount"),
                StatusCode = statusCode,
                StateCode = stateCode,
                StatusLabel = SalesOrderFieldCatalog.GetStatusLabel(statusCode),
                StateLabel = SalesOrderFieldCatalog.GetStateLabel(stateCode)
            });
        }

        return list;
    }

    private static List<CrmAccount> ParseAccounts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var values))
        {
            return [];
        }

        var list = new List<CrmAccount>();
        foreach (var item in values.EnumerateArray())
        {
            list.Add(new CrmAccount
            {
                AccountId = GetGuid(item, "accountid") ?? Guid.Empty,
                Name = GetString(item, "name") ?? string.Empty,
                AccountNumber = GetString(item, "accountnumber"),
                Telephone1 = GetString(item, "telephone1"),
                EmailAddress1 = GetString(item, "emailaddress1"),
                City = GetString(item, "address1_city"),
                Country = GetString(item, "address1_country"),
                Industry = GetString(item, "industrycode"),
                IsConfidential = false
            });
        }

        return list.Where(a => a.AccountId != Guid.Empty).ToList();
    }

    private static List<CrmContact> ParseContacts(string json, Guid accountId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var values))
        {
            return [];
        }

        var list = new List<CrmContact>();
        foreach (var item in values.EnumerateArray())
        {
            list.Add(new CrmContact
            {
                ContactId = GetGuid(item, "contactid") ?? Guid.Empty,
                AccountId = GetGuid(item, "_parentcustomerid_value") ?? accountId,
                FullName = GetString(item, "fullname") ?? string.Empty,
                EmailAddress1 = GetString(item, "emailaddress1"),
                Telephone1 = GetString(item, "telephone1"),
                JobTitle = GetString(item, "jobtitle")
            });
        }

        return list.Where(c => c.ContactId != Guid.Empty).ToList();
    }

    private static Guid? GetGuid(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.TryGetGuid(out var guid) ? guid : null;

    private static string? GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int? GetInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var value) ? value : null;

    private static DateTimeOffset? GetDate(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.TryGetDateTimeOffset(out var value) ? value : null;

    private static decimal? GetDecimal(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var d))
        {
            return d;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            decimal.TryParse(prop.GetString(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
