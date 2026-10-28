using System.ComponentModel;
using System.Text.Json;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Models;
using CrmAiPoc.Api.Crm.Query;
using CrmAiPoc.Api.Crm.Validation;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Approved read-only CRM tools exposed to the AI model via Microsoft.Extensions.AI.
/// The model may only select these functions; all validation and auth run server-side.
/// </summary>
public sealed class CrmAiTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ICrmDataProvider _crm;
    private readonly ICrmAuthorizationService _auth;
    private readonly CrmArgumentValidator _validator;
    private readonly CrmOptions _crmOptions;
    private readonly IGenericCrmRepository? _genericCrm;
    private readonly ICrmMetadataService? _metadata;
    private readonly ILogger<CrmAiTools> _logger;

    // Set per-request by ChatService before invocation.
    public ConversationSession? Session { get; set; }

    public CrmAiTools(
        ICrmDataProvider crm,
        ICrmAuthorizationService auth,
        CrmArgumentValidator validator,
        IOptions<CrmOptions> crmOptions,
        ILogger<CrmAiTools> logger,
        IGenericCrmRepository? genericCrm = null,
        ICrmMetadataService? metadata = null)
    {
        _crm = crm;
        _auth = auth;
        _validator = validator;
        _crmOptions = crmOptions.Value;
        _genericCrm = genericCrm;
        _metadata = metadata;
        _logger = logger;
    }

    [Description("Search CRM accounts by a partial name match. Use for questions about finding or listing accounts. Returns account id, name, and basic contact fields. Read-only.")]
    public async Task<string> search_accounts(
        [Description("Partial account name to search for (required).")] string name,
        [Description("Maximum number of accounts to return (1-25). Default 10.")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Selected AI function: search_accounts | Parsed arguments: nameLength={NameLength}, limit={Limit}",
            name?.Length ?? 0,
            limit);

        var auth = _auth.AuthorizeRead(CrmEntityAllowlist.Account, "search_accounts");
        if (!auth.Allowed)
        {
            return Fail(auth.Reason);
        }

        var nameValidation = _validator.ValidateSearchName(name);
        if (!nameValidation.IsValid)
        {
            _logger.LogInformation("search_accounts validation failed: {Error}", nameValidation.Error);
            return Fail(nameValidation.Error!);
        }

        var safeLimit = _validator.ClampLimit(limit);
        var result = await _crm.SearchAccountsAsync(nameValidation.NormalizedValue!, safeLimit, cancellationToken);

        // Filter confidential / denied records after retrieval (defense in depth).
        var allowed = new List<object>();
        foreach (var account in result.Items)
        {
            var accountAuth = _auth.AuthorizeAccountAccess(account, "search_accounts");
            if (!accountAuth.Allowed)
            {
                continue;
            }

            allowed.Add(ProjectAccount(account));
        }

        if (Session is not null && allowed.Count == 1 && result.Items.Count > 0)
        {
            var firstAllowed = result.Items.FirstOrDefault(a =>
                _auth.AuthorizeAccountAccess(a, "search_accounts").Allowed);
            if (firstAllowed is not null)
            {
                Session.LastReferencedAccountId = firstAllowed.AccountId;
                Session.LastReferencedAccountName = firstAllowed.Name;
            }
        }

        var payload = new
        {
            function = "search_accounts",
            provider = _crm.ProviderName,
            query = nameValidation.NormalizedValue,
            limit = safeLimit,
            totalMatchedVisible = allowed.Count,
            accounts = allowed,
            note = allowed.Count == 0
                ? (result.Message ?? "No accounts matched, or matched records were not authorized.")
                : null
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _logger.LogInformation(
            "Function execution result: search_accounts returned {Count} authorized account(s).",
            allowed.Count);
        return json;
    }

    [Description("Get contacts related to a specific CRM account by accountId (GUID). For follow-ups like 'contacts for this account', use the accountId from conversation context when unambiguous. Read-only.")]
    public async Task<string> get_contacts_for_account(
        [Description("The account GUID. Required unless conversation context has exactly one recent account.")] string? accountId = null,
        [Description("Maximum number of contacts to return (1-25). Default 10.")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Selected AI function: get_contacts_for_account | Parsed arguments: accountIdProvided={Provided}, limit={Limit}",
            !string.IsNullOrWhiteSpace(accountId),
            limit);

        var auth = _auth.AuthorizeRead(CrmEntityAllowlist.Contact, "get_contacts_for_account");
        if (!auth.Allowed)
        {
            return Fail(auth.Reason);
        }

        var resolvedId = accountId;
        if (string.IsNullOrWhiteSpace(resolvedId) && Session?.LastReferencedAccountId is Guid lastId)
        {
            resolvedId = lastId.ToString();
            _logger.LogInformation(
                "Resolved accountId from conversation context: {AccountId}",
                lastId);
        }

        var idValidation = _validator.ValidateAccountId(resolvedId);
        if (!idValidation.IsValid)
        {
            _logger.LogInformation("get_contacts_for_account validation failed: {Error}", idValidation.Error);
            return Fail(idValidation.Error! + " Ask the user which account they mean if the reference is ambiguous.");
        }

        var accountGuid = Guid.Parse(idValidation.NormalizedValue!);

        // Authorization for the parent account before returning contacts.
        var accountAuth = _auth.AuthorizeAccountId(accountGuid, isConfidential: false, "get_contacts_for_account");
        // Still block the known denied confidential id even if confidentiality flag is unknown here.
        if (_auth.CurrentUser.DeniedAccountIds.Contains(accountGuid))
        {
            accountAuth = AuthorizationDecision.Deny(
                "Access denied: you do not have permission to read this confidential CRM record.",
                "get_contacts_for_account",
                CrmEntityAllowlist.Account);
            _logger.LogInformation(
                "Authorization decision: Allowed={Allowed}, Operation={Operation}, Reason={Reason}",
                accountAuth.Allowed,
                accountAuth.Operation,
                accountAuth.Reason);
            return Fail(accountAuth.Reason);
        }

        if (!accountAuth.Allowed)
        {
            return Fail(accountAuth.Reason);
        }

        var safeLimit = _validator.ClampLimit(limit);
        var result = await _crm.GetContactsForAccountAsync(accountGuid, safeLimit, cancellationToken);

        var contacts = result.Items.Select(ProjectContact).ToList();

        if (contacts.Count > 0 && Session is not null)
        {
            Session.LastReferencedAccountId = accountGuid;
        }

        var payload = new
        {
            function = "get_contacts_for_account",
            provider = _crm.ProviderName,
            accountId = accountGuid,
            limit = safeLimit,
            totalMatched = result.TotalMatched,
            contacts,
            note = contacts.Count == 0 ? result.Message : null
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _logger.LogInformation(
            "Function execution result: get_contacts_for_account returned {Count} contact(s).",
            contacts.Count);
        return json;
    }

    [Description("Count CRM accounts/customers only (الحسابات/العملاء). Do NOT use for Sales Orders / أوامر البيع — use query_sales_orders with operation=count instead. Read-only.")]
    public async Task<string> count_accounts(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Selected AI function: count_accounts | Parsed arguments: (none)");

        var auth = _auth.AuthorizeRead(CrmEntityAllowlist.Account, "count_accounts");
        if (!auth.Allowed)
        {
            return Fail(auth.Reason);
        }

        var count = await _crm.CountAccountsAsync(cancellationToken);
        var payload = new
        {
            function = "count_accounts",
            provider = _crm.ProviderName,
            count,
            note = "Count reflects records visible under current server-side permissions and provider rules."
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _logger.LogInformation("Function execution result: count_accounts returned count={Count}.", count);
        return json;
    }

    [Description("Get recent CRM sales orders (read-only). Prefer query_sales_orders for filtered/sorted/count questions. Use for simple 'latest N sales orders' / أوامر البيع. Returns allowlisted order fields only.")]
    public async Task<string> get_sales_orders(
        [Description("Maximum number of sales orders to return (1-25). Default 10.")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Selected AI function: get_sales_orders | Parsed arguments: limit={Limit}",
            limit);

        var auth = _auth.AuthorizeRead(CrmEntityAllowlist.SalesOrder, "get_sales_orders");
        if (!auth.Allowed)
        {
            return Fail(auth.Reason);
        }

        // Live CRM rows may be retrieved for local/privacy-safe chat, but must never be
        // forwarded to an external LLM while AllowSendLiveDataToAi remains false.
        if (_crm.ProviderName.Equals("Dynamics365", StringComparison.OrdinalIgnoreCase) &&
            _crmOptions.AllowSendLiveDataToAi)
        {
            _logger.LogWarning(
                "AllowSendLiveDataToAi=true is unexpected for this POC privacy path; refusing automatic external transmission policy.");
            return Fail(
                "External transmission of live CRM data is not enabled for this POC. Keep Crm:AllowSendLiveDataToAi=false.");
        }

        var safeLimit = _validator.ClampLimit(limit);
        var result = await _crm.GetSalesOrdersAsync(safeLimit, cancellationToken);
        var orders = result.Items.Select(ProjectSalesOrder).ToList();

        var payload = new
        {
            function = "get_sales_orders",
            provider = _crm.ProviderName,
            limit = safeLimit,
            totalMatched = result.TotalMatched,
            salesOrders = orders,
            note = orders.Count == 0 ? result.Message : null
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _logger.LogInformation(
            "Function execution result: get_sales_orders returned {Count} order(s).",
            orders.Count);
        return json;
    }

    [Description("Query Dynamics salesorder records with structured filters (read-only). REQUIRED for Sales Order count questions such as 'عدد ال Sales Order', 'كم عدد أوامر البيع', 'how many sales orders'. Set operation=count for totals. Also use for filtered/dated/sorted/amount/status questions in English or Arabic. Never invent OData/SQL/FetchXML. Only salesorder entity. " + SalesOrderFieldCatalogAiHint.Text)]
    public async Task<string> query_sales_orders(
        [Description("Use 'count' for عدد / how many / totals. Use 'list' to return rows.")] string operation = "list",
        [Description("JSON array of up to 3 filters: [{field,operator,value}]. Operators: eq,ne,gt,ge,lt,le,contains. Example: [{\"field\":\"totalamount\",\"operator\":\"gt\",\"value\":\"10000\"}]")] string? filtersJson = null,
        [Description("Date field for relative range: createdon or modifiedon.")] string? relativeDateField = null,
        [Description("Relative date token resolved by backend: last_calendar_month, this_calendar_month, this_week, last_7_days, last_30_days.")] string? relativeDateToken = null,
        [Description("Sort field: createdon or totalamount.")] string? sortField = null,
        [Description("Sort direction: asc or desc. Default desc.")] string? sortDirection = "desc",
        [Description("Max rows for list (1-50). Default 10. Ignored for count.")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Selected AI function: query_sales_orders | operation={Operation}, hasFilters={HasFilters}, relative={Relative}, sort={Sort}, limit={Limit}",
            operation,
            !string.IsNullOrWhiteSpace(filtersJson),
            relativeDateToken,
            sortField,
            limit);

        var auth = _auth.AuthorizeRead(CrmEntityAllowlist.SalesOrder, "query_sales_orders");
        if (!auth.Allowed)
        {
            return Fail(auth.Reason);
        }

        if (_crm.ProviderName.Equals("Dynamics365", StringComparison.OrdinalIgnoreCase) &&
            _crmOptions.AllowSendLiveDataToAi)
        {
            return Fail(
                "External transmission of live CRM data is not enabled for this POC. Keep Crm:AllowSendLiveDataToAi=false.");
        }

        var parsed = SalesOrderQueryPlanParser.Parse(
            operation,
            filtersJson,
            relativeDateField,
            relativeDateToken,
            sortField,
            sortDirection,
            limit);
        if (!parsed.Success || parsed.Plan is null)
        {
            return Fail(parsed.Error ?? "Invalid query arguments.");
        }

        var result = await _crm.QuerySalesOrdersAsync(parsed.Plan, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Message) &&
            result.Items.Count == 0 &&
            result.Count is null &&
            string.IsNullOrWhiteSpace(result.RelativeRequestUri))
        {
            return Fail(result.Message);
        }

        var payload = new
        {
            function = "query_sales_orders",
            provider = result.ProviderName,
            operation = result.Operation.ToString().ToLowerInvariant(),
            appliedFilters = result.AppliedFilterSummary,
            sort = result.SortSummary,
            limit = result.Limit,
            count = result.Count,
            countMayBeCapped = result.CountMayBeCapped,
            totalReturned = result.Items.Count,
            salesOrders = result.Items.Select(ProjectSalesOrder).ToList(),
            note = result.Message
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _logger.LogInformation(
            "Function execution result: query_sales_orders operation={Operation}, returned={Count}, count={ODataCount}",
            result.Operation,
            result.Items.Count,
            result.Count);
        return json;
    }

    private static object ProjectAccount(CrmAccount account) => new
    {
        accountId = account.AccountId,
        name = account.Name,
        accountNumber = account.AccountNumber,
        telephone1 = account.Telephone1,
        emailAddress1 = account.EmailAddress1,
        city = account.City,
        country = account.Country,
        industry = account.Industry
    };

    private static object ProjectContact(CrmContact contact) => new
    {
        contactId = contact.ContactId,
        accountId = contact.AccountId,
        fullName = contact.FullName,
        emailAddress1 = contact.EmailAddress1,
        telephone1 = contact.Telephone1,
        jobTitle = contact.JobTitle
    };

    private static object ProjectSalesOrder(CrmSalesOrder order) => new
    {
        salesOrderId = order.SalesOrderId,
        name = order.Name,
        orderNumber = order.OrderNumber,
        customerAccountId = order.CustomerAccountId,
        createdOn = order.CreatedOn,
        modifiedOn = order.ModifiedOn,
        totalAmount = order.TotalAmount,
        statusCode = order.StatusCode,
        statusLabel = order.StatusLabel,
        stateCode = order.StateCode,
        stateLabel = order.StateLabel
    };

    [Description("THE only CRM read tool. Query ANY approved entity (account, contact, salesorder, lead, …). Supports list/count/filters/sort/dates/limit and one-hop relatedFromEntity+relatedFromId. Pass structured plan only — never OData/SQL/FetchXML.")]
    public async Task<string> query_crm(
        [Description("Approved entity logical name: account | contact | salesorder | lead (or configured aliases).")] string entity,
        [Description("list or count")] string operation = "list",
        [Description("Optional JSON array of field names to select.")] string? selectJson = null,
        [Description("JSON array of filters: [{field,operator,value}] max 3.")] string? filtersJson = null,
        [Description("Date field for relative range.")] string? relativeDateField = null,
        [Description("last_calendar_month | this_calendar_month | this_week | last_7_days | last_30_days")] string? relativeDateToken = null,
        [Description("JSON array of sorts: [{field,direction}] max 2.")] string? sortJson = null,
        [Description("Legacy single sort field.")] string? sortField = null,
        [Description("asc or desc")] string? sortDirection = "desc",
        [Description("Max rows 1-50 for list.")] int limit = 10,
        [Description("Parent entity for one-hop relationship filter.")] string? relatedFromEntity = null,
        [Description("Parent record GUID for one-hop relationship filter.")] string? relatedFromId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Selected AI function: query_crm | entity={Entity}, operation={Operation}, limit={Limit}",
            entity,
            operation,
            limit);

        if (_genericCrm is null)
        {
            return Fail("Generic CRM query engine is not registered.");
        }

        if (_crm.ProviderName.Equals("Dynamics365", StringComparison.OrdinalIgnoreCase) &&
            _crmOptions.AllowSendLiveDataToAi)
        {
            return Fail("External transmission of live CRM data is not enabled for this POC.");
        }

        if (_metadata is not null)
        {
            await _metadata.EnsureApprovedEntitiesLoadedAsync(cancellationToken);
        }

        var parsed = CrmQueryPlanParser.Parse(
            entity,
            operation,
            selectJson,
            filtersJson,
            relativeDateField,
            relativeDateToken,
            sortJson,
            sortField,
            sortDirection,
            limit,
            relatedFromEntity,
            relatedFromId);
        if (!parsed.Success || parsed.Plan is null)
        {
            return Fail(parsed.Error ?? "Invalid query_crm arguments.");
        }

        var result = await _genericCrm.QueryAsync(parsed.Plan, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Message) &&
            string.IsNullOrWhiteSpace(result.RelativeRequestUri) &&
            result.Items.Count == 0 &&
            result.Count is null)
        {
            return Fail(result.Message);
        }

        var payload = new
        {
            function = "query_crm",
            provider = result.ProviderName,
            entity = result.Entity,
            operation = result.Operation.ToString().ToLowerInvariant(),
            appliedFilters = result.AppliedFilterSummary,
            sort = result.SortSummary,
            limit = result.Limit,
            count = result.Count,
            countMayBeCapped = result.CountMayBeCapped,
            totalReturned = result.Items.Count,
            records = result.Items,
            note = result.Message,
            catalogHint = _metadata?.GetAiCatalogSummary()
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _logger.LogInformation(
            "Function execution result: query_crm entity={Entity} operation={Operation} rows={Rows} count={Count}",
            result.Entity,
            result.Operation,
            result.Items.Count,
            result.Count);
        return json;
    }

    private string Fail(string message)
    {
        _logger.LogInformation("Function execution result: failure — {Message}", message);
        return JsonSerializer.Serialize(new { error = true, message }, JsonOptions);
    }
}
