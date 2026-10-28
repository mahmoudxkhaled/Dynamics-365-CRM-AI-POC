using System.Diagnostics;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Mock;
using CrmAiPoc.Api.Crm.Query;
using CrmAiPoc.Api.Diagnostics;
using CrmAiPoc.Api.Security;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// In-memory generic repository backed by mock seed data for account/contact/salesorder.
/// </summary>
public sealed class MockGenericCrmRepository : IGenericCrmRepository
{
    private readonly ICrmMetadataService _metadata;
    private readonly EntityApprovalPolicy _policy;
    private readonly IGenericCrmQueryBuilder _builder;
    private readonly ICrmAuthorizationService _auth;
    private readonly IExecutionTracer _tracer;

    public MockGenericCrmRepository(
        ICrmMetadataService metadata,
        EntityApprovalPolicy policy,
        IGenericCrmQueryBuilder builder,
        ICrmAuthorizationService auth,
        IExecutionTracer? tracer = null)
    {
        _metadata = metadata;
        _policy = policy;
        _builder = builder;
        _auth = auth;
        _tracer = tracer ?? NullExecutionTracer.Instance;
    }

    public string ProviderName => "Mock";

    public async Task<GenericCrmQueryResult> QueryAsync(CrmQueryPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_policy.TryResolveEntityAlias(plan.Entity, out var logical))
        {
            logical = plan.Entity.Trim();
        }

        if (!_policy.IsApproved(logical))
        {
            return Fail(plan, $"Entity '{plan.Entity}' is not an approved AI-accessible CRM entity.");
        }

        var auth = _auth.AuthorizeRead(logical, "query_crm");
        if (!auth.Allowed)
        {
            return Fail(plan, auth.Reason);
        }

        var entity = await _metadata.GetEntityAsync(logical, cancellationToken);
        if (entity is null)
        {
            return Fail(plan, $"Metadata for '{logical}' is unavailable.");
        }

        CrmEntityMetadata? relatedFrom = null;
        if (!string.IsNullOrWhiteSpace(plan.RelatedFromEntity))
        {
            var fromName = _policy.TryResolveEntityAlias(plan.RelatedFromEntity, out var resolved)
                ? resolved
                : plan.RelatedFromEntity.Trim();
            relatedFrom = await _metadata.GetEntityAsync(fromName, cancellationToken);
        }

        plan = new CrmQueryPlan
        {
            Entity = logical,
            Operation = plan.Operation,
            Select = plan.Select,
            Filters = plan.Filters,
            RelativeDate = plan.RelativeDate,
            Sort = plan.Sort,
            Limit = plan.Limit,
            RelatedFromEntity = plan.RelatedFromEntity,
            RelatedFromId = plan.RelatedFromId
        };

        var buildSw = Stopwatch.StartNew();
        var buildStep = _tracer.Begin(ExecutionSteps.QueryConstruction, "Building OData query from plan (Mock)");
        var built = _builder.Build(plan, entity, relatedFrom);
        buildSw.Stop();
        if (!built.Success || string.IsNullOrWhiteSpace(built.RelativeRequestUri))
        {
            if (buildStep is not null)
            {
                _tracer.Complete(buildStep.StepId, ExecutionStepStatus.Failed, error: built.Error);
            }

            return Fail(plan, built.Error ?? "Invalid CRM query plan.");
        }

        var safePath = TraceSanitizer.RedactODataPath(built.RelativeRequestUri);
        var crmQuery = new
        {
            method = "OData",
            httpMethod = "GET",
            entity = logical,
            entitySetName = entity.EntitySetName,
            operation = built.Operation.ToString(),
            selectFields = built.SelectFields,
            filters = built.AppliedFilterSummary,
            sort = built.SortSummary,
            limit = built.Limit,
            relatedFromEntity = plan.RelatedFromEntity,
            relativeRequestUri = safePath,
            readableQuery = safePath,
            provider = ProviderName
        };
        _tracer.SetCrmQuery(crmQuery);
        if (buildStep is not null)
        {
            _tracer.Complete(buildStep.StepId, ExecutionStepStatus.Success, summary: safePath, details: crmQuery);
        }

        var mockHttpSw = Stopwatch.StartNew();
        var reqStep = _tracer.Begin(ExecutionSteps.DynamicsHttpRequest, "Mock CRM read");
        var rows = LoadRows(logical);
        rows = ApplyRelationship(rows, plan, entity, relatedFrom);
        rows = ApplyRelative(rows, plan);
        rows = ApplyFilters(rows, plan, entity);
        mockHttpSw.Stop();
        _tracer.SetMetric("crmHttpMs", mockHttpSw.ElapsedMilliseconds);
        if (reqStep is not null)
        {
            _tracer.Complete(
                reqStep.StepId,
                ExecutionStepStatus.Success,
                summary: $"Mock GET matched={rows.Count}",
                details: new { httpStatus = 200, durationMs = mockHttpSw.ElapsedMilliseconds });
        }

        _tracer.Instant(
            ExecutionSteps.DynamicsHttpResponse,
            ExecutionStepStatus.Success,
            summary: "Mock CRM response (counts only; rows not stored in trace)",
            details: new { httpStatus = 200, provider = ProviderName, matched = rows.Count },
            durationMs: mockHttpSw.ElapsedMilliseconds);

        if (plan.Operation == CrmQueryOperation.Count)
        {
            _tracer.SetCrmQuery(new
            {
                crmQuery.method,
                crmQuery.httpMethod,
                crmQuery.entity,
                crmQuery.entitySetName,
                crmQuery.operation,
                crmQuery.selectFields,
                crmQuery.filters,
                crmQuery.sort,
                crmQuery.limit,
                crmQuery.relativeRequestUri,
                httpStatus = 200,
                recordCount = rows.Count
            });
            return new GenericCrmQueryResult
            {
                Entity = logical,
                Operation = CrmQueryOperation.Count,
                RelativeRequestUri = built.RelativeRequestUri,
                AppliedFilterSummary = built.AppliedFilterSummary,
                Count = rows.Count,
                ProviderName = ProviderName,
                PrimaryIdAttribute = entity.PrimaryIdAttribute,
                PrimaryNameAttribute = entity.PrimaryNameAttribute
            };
        }

        rows = ApplySort(rows, plan, entity).Take(built.Limit).ToList();
        _tracer.SetCrmQuery(new
        {
            crmQuery.method,
            crmQuery.httpMethod,
            crmQuery.entity,
            crmQuery.entitySetName,
            crmQuery.operation,
            crmQuery.selectFields,
            crmQuery.filters,
            crmQuery.sort,
            crmQuery.limit,
            crmQuery.relativeRequestUri,
            httpStatus = 200,
            recordCount = rows.Count
        });
        return new GenericCrmQueryResult
        {
            Entity = logical,
            Operation = CrmQueryOperation.List,
            RelativeRequestUri = built.RelativeRequestUri,
            AppliedFilterSummary = built.AppliedFilterSummary,
            SortSummary = built.SortSummary,
            Limit = built.Limit,
            Items = rows,
            Message = rows.Count == 0 ? "No records matched the query." : null,
            ProviderName = ProviderName,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute
        };
    }

    private static List<IReadOnlyDictionary<string, object?>> LoadRows(string logical) =>
        logical.ToLowerInvariant() switch
        {
            "account" => MockCrmSeedData.CreateAccounts().Select(a => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["accountid"] = a.AccountId,
                ["name"] = a.Name,
                ["accountnumber"] = a.AccountNumber,
                ["telephone1"] = a.Telephone1,
                ["emailaddress1"] = a.EmailAddress1,
                ["address1_city"] = a.City,
                ["address1_country"] = a.Country,
                ["createdon"] = DateTimeOffset.UtcNow.AddDays(-30)
            }).ToList(),
            "contact" => MockCrmSeedData.CreateContacts().Select(c => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["contactid"] = c.ContactId,
                ["fullname"] = c.FullName,
                ["emailaddress1"] = c.EmailAddress1,
                ["telephone1"] = c.Telephone1,
                ["jobtitle"] = c.JobTitle,
                ["_parentcustomerid_value"] = c.AccountId,
                ["createdon"] = DateTimeOffset.UtcNow.AddDays(-20)
            }).ToList(),
            "salesorder" => MockCrmSeedData.CreateSalesOrders().Select(o => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["salesorderid"] = o.SalesOrderId,
                ["name"] = o.Name,
                ["ordernumber"] = o.OrderNumber,
                ["createdon"] = o.CreatedOn,
                ["modifiedon"] = o.ModifiedOn,
                ["totalamount"] = o.TotalAmount,
                ["statuscode"] = o.StatusCode,
                ["statuscode_label"] = o.StatusLabel,
                ["statecode"] = o.StateCode,
                ["statecode_label"] = o.StateLabel,
                ["_te_customeraccountid_value"] = o.CustomerAccountId
            }).ToList(),
            _ => []
        };

    private static List<IReadOnlyDictionary<string, object?>> ApplyRelationship(
        List<IReadOnlyDictionary<string, object?>> rows,
        CrmQueryPlan plan,
        CrmEntityMetadata entity,
        CrmEntityMetadata? relatedFrom)
    {
        if (string.IsNullOrWhiteSpace(plan.RelatedFromEntity) || string.IsNullOrWhiteSpace(plan.RelatedFromId))
        {
            return rows;
        }

        var hop = CrmRelationshipResolver.ResolveOneHop(entity, plan.RelatedFromEntity, plan.RelatedFromId, relatedFrom);
        if (!hop.Success)
        {
            return [];
        }

        return rows.Where(r =>
            r.TryGetValue(hop.FilterField!, out var v) &&
            string.Equals(Convert.ToString(v), hop.FilterValue, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static List<IReadOnlyDictionary<string, object?>> ApplyRelative(
        List<IReadOnlyDictionary<string, object?>> rows,
        CrmQueryPlan plan)
    {
        if (plan.RelativeDate is null)
        {
            return rows;
        }

        var tz = BusinessTimeZone.Resolve();
        var (start, end, _) = Query.RelativeDateResolver.Resolve(plan.RelativeDate.Token, DateTimeOffset.UtcNow, tz);
        return rows.Where(r =>
        {
            if (!r.TryGetValue(plan.RelativeDate.Field, out var v) || v is not DateTimeOffset dto)
            {
                return false;
            }

            return dto >= start && dto < end;
        }).ToList();
    }

    private static List<IReadOnlyDictionary<string, object?>> ApplyFilters(
        List<IReadOnlyDictionary<string, object?>> rows,
        CrmQueryPlan plan,
        CrmEntityMetadata entity)
    {
        foreach (var filter in plan.Filters)
        {
            if (!entity.TryGetAttribute(filter.Field, out var attr))
            {
                continue;
            }

            rows = rows.Where(r => Match(r, attr, filter)).ToList();
        }

        return rows;
    }

    private static bool Match(
        IReadOnlyDictionary<string, object?> row,
        CrmAttributeMetadata attr,
        CrmQueryFilter filter)
    {
        row.TryGetValue(attr.ODataPropertyName, out var raw);
        raw ??= row.TryGetValue(attr.LogicalName, out var alt) ? alt : null;
        var text = Convert.ToString(raw) ?? string.Empty;

        if (filter.Operator == CrmFilterOperator.Contains)
        {
            return text.Contains(filter.Value, StringComparison.OrdinalIgnoreCase);
        }

        if (attr.Kind is CrmAttributeKind.Money or CrmAttributeKind.Decimal or CrmAttributeKind.Integer &&
            decimal.TryParse(filter.Value, out var expected) &&
            decimal.TryParse(text, out var actual))
        {
            return filter.Operator switch
            {
                CrmFilterOperator.Gt => actual > expected,
                CrmFilterOperator.Ge => actual >= expected,
                CrmFilterOperator.Lt => actual < expected,
                CrmFilterOperator.Le => actual <= expected,
                CrmFilterOperator.Ne => actual != expected,
                _ => actual == expected
            };
        }

        if (attr.Kind is CrmAttributeKind.Status or CrmAttributeKind.State or CrmAttributeKind.Picklist)
        {
            var code = attr.OptionSetByLabel.TryGetValue(filter.Value, out var c) ? c.ToString() : filter.Value;
            return filter.Operator == CrmFilterOperator.Ne
                ? !text.Equals(code, StringComparison.OrdinalIgnoreCase)
                : text.Equals(code, StringComparison.OrdinalIgnoreCase);
        }

        return filter.Operator == CrmFilterOperator.Ne
            ? !text.Equals(filter.Value, StringComparison.OrdinalIgnoreCase)
            : text.Equals(filter.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> ApplySort(
        List<IReadOnlyDictionary<string, object?>> rows,
        CrmQueryPlan plan,
        CrmEntityMetadata entity)
    {
        var field = plan.Sort.FirstOrDefault()?.Field
                    ?? (entity.TryGetAttribute("createdon", out _) ? "createdon" : entity.PrimaryNameAttribute);
        var desc = plan.Sort.FirstOrDefault()?.Direction != CrmSortDirection.Asc;
        entity.TryGetAttribute(field, out var attr);
        var key = attr?.ODataPropertyName ?? field;

        return desc
            ? rows.OrderByDescending(r => r.TryGetValue(key, out var v) ? v?.ToString() : null)
            : rows.OrderBy(r => r.TryGetValue(key, out var v) ? v?.ToString() : null);
    }

    private static GenericCrmQueryResult Fail(CrmQueryPlan plan, string message) =>
        new()
        {
            Entity = plan.Entity,
            Operation = plan.Operation,
            RelativeRequestUri = string.Empty,
            Message = message,
            ProviderName = "Mock"
        };
}
