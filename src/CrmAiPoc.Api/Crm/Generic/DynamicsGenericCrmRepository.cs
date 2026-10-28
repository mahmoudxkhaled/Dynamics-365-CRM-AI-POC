using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Diagnostics;
using CrmAiPoc.Api.Security;

namespace CrmAiPoc.Api.Crm.Generic;

public sealed class DynamicsGenericCrmRepository : IGenericCrmRepository
{
    private readonly DynamicsCrmHttp _http;
    private readonly ICrmMetadataService _metadata;
    private readonly EntityApprovalPolicy _policy;
    private readonly IGenericCrmQueryBuilder _builder;
    private readonly ICrmAuthorizationService _auth;
    private readonly IExecutionTracer _tracer;
    private readonly ILogger<DynamicsGenericCrmRepository> _logger;

    public DynamicsGenericCrmRepository(
        DynamicsCrmHttp http,
        ICrmMetadataService metadata,
        EntityApprovalPolicy policy,
        IGenericCrmQueryBuilder builder,
        ICrmAuthorizationService auth,
        IExecutionTracer tracer,
        ILogger<DynamicsGenericCrmRepository> logger)
    {
        _http = http;
        _metadata = metadata;
        _policy = policy;
        _builder = builder;
        _auth = auth;
        _tracer = tracer;
        _logger = logger;
    }

    public string ProviderName => "Dynamics365";

    public async Task<GenericCrmQueryResult> QueryAsync(CrmQueryPlan plan, CancellationToken cancellationToken = default)
    {
        if (!_policy.TryResolveEntityAlias(plan.Entity, out var logical) &&
            !_policy.IsApproved(plan.Entity))
        {
            return Fail(plan, $"Entity '{plan.Entity}' is not an approved AI-accessible CRM entity.");
        }

        if (!_policy.IsApproved(logical) && _policy.IsApproved(plan.Entity))
        {
            logical = plan.Entity.Trim();
        }

        var auth = _auth.AuthorizeRead(logical, "query_crm");
        if (!auth.Allowed)
        {
            return Fail(plan, auth.Reason);
        }

        var entity = await _metadata.GetEntityAsync(logical, cancellationToken);
        if (entity is null || !entity.IsApprovedForAi)
        {
            return Fail(plan, $"Metadata for approved entity '{logical}' is unavailable.");
        }

        CrmEntityMetadata? relatedFrom = null;
        if (!string.IsNullOrWhiteSpace(plan.RelatedFromEntity))
        {
            if (!_policy.IsApproved(plan.RelatedFromEntity) &&
                !_policy.TryResolveEntityAlias(plan.RelatedFromEntity, out _))
            {
                return Fail(plan, $"relatedFromEntity '{plan.RelatedFromEntity}' is not approved.");
            }

            var fromName = _policy.TryResolveEntityAlias(plan.RelatedFromEntity, out var resolved)
                ? resolved
                : plan.RelatedFromEntity.Trim();
            relatedFrom = await _metadata.GetEntityAsync(fromName, cancellationToken);
        }

        plan = CloneWithEntity(plan, logical);
        var buildSw = Stopwatch.StartNew();
        var buildStep = _tracer.Begin(ExecutionSteps.QueryConstruction, "Building OData query from plan");
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
            readableQuery = safePath
        };
        _tracer.SetCrmQuery(crmQuery);
        if (buildStep is not null)
        {
            _tracer.Complete(
                buildStep.StepId,
                ExecutionStepStatus.Success,
                summary: safePath.Length > 120 ? safePath[..120] + "…" : safePath,
                details: crmQuery);
        }

        _http.EnsureLiveConfigured();
        _logger.LogInformation(
            "query_crm prepared. Entity={Entity}, Operation={Operation}, Limit={Limit}",
            logical,
            built.Operation,
            built.Limit);

        var includeAnnotations = built.Operation == CrmQueryOperation.Count;
        var reqStep = _tracer.Begin(
            ExecutionSteps.DynamicsHttpRequest,
            summary: $"GET {entity.EntitySetName}",
            details: new { httpMethod = "GET", path = safePath, includeAnnotations });
        var crmSw = Stopwatch.StartNew();
        var http = await _http.GetAsync(built.RelativeRequestUri, includeAnnotations, cancellationToken);
        if (!http.IsSuccess && includeAnnotations)
        {
            // Fall back without Prefer if annotations request fails on this path.
            _logger.LogWarning(
                "Count query with annotations failed ({Status}); retrying without Prefer.",
                http.StatusCode);
            http = await _http.GetAsync(built.RelativeRequestUri, includeAnnotations: false, cancellationToken);
        }

        crmSw.Stop();
        _tracer.SetMetric("crmHttpMs", crmSw.ElapsedMilliseconds);
        if (reqStep is not null)
        {
            _tracer.Complete(
                reqStep.StepId,
                http.IsSuccess ? ExecutionStepStatus.Success : ExecutionStepStatus.Failed,
                summary: $"status={http.StatusCode}; {crmSw.ElapsedMilliseconds}ms",
                details: new { http.StatusCode, durationMs = crmSw.ElapsedMilliseconds, path = safePath },
                error: http.IsSuccess ? null : http.ErrorCategory);
        }

        _tracer.Instant(
            ExecutionSteps.DynamicsHttpResponse,
            http.IsSuccess ? ExecutionStepStatus.Success : ExecutionStepStatus.Failed,
            summary: http.IsSuccess
                ? $"CRM HTTP {http.StatusCode} (body length only; rows not stored in trace)"
                : $"CRM HTTP {http.StatusCode} failed",
            details: new
            {
                http.StatusCode,
                bodyLength = http.Body?.Length ?? 0,
                durationMs = crmSw.ElapsedMilliseconds,
                // Never attach raw CRM body to the inspector.
                recordsStoredInTrace = false
            },
            error: http.IsSuccess ? null : $"Dynamics GET failed ({http.StatusCode})",
            durationMs: crmSw.ElapsedMilliseconds);

        if (!http.IsSuccess)
        {
            throw new HttpRequestException(
                $"Dynamics Web API GET failed. Status={http.StatusCode}, Path={built.RelativeRequestUri}.");
        }

        if (built.Operation == CrmQueryOperation.Count)
        {
            var countInfo = ReadODataCountInfo(http.Body);
            var capped = countInfo.LimitExceeded ||
                         countInfo.Count >= CrmQueryLimits.DynamicsODataCountCeiling;
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
                crmQuery.relatedFromEntity,
                crmQuery.relativeRequestUri,
                crmQuery.readableQuery,
                httpStatus = http.StatusCode,
                recordCount = countInfo.Count,
                countMayBeCapped = capped,
                durationMs = crmSw.ElapsedMilliseconds
            });
            return new GenericCrmQueryResult
            {
                Entity = logical,
                Operation = CrmQueryOperation.Count,
                RelativeRequestUri = built.RelativeRequestUri,
                AppliedFilterSummary = built.AppliedFilterSummary,
                SortSummary = built.SortSummary,
                Limit = built.Limit,
                Count = countInfo.Count,
                CountMayBeCapped = capped,
                CountLimitExceededAnnotation = countInfo.LimitExceeded,
                Message = capped
                    ? $"CRM reported count {countInfo.Count} with limit exceeded (v9.1 @odata.count / totalrecordcountlimitexceeded). This is not an exact total."
                    : null,
                ProviderName = ProviderName,
                PrimaryIdAttribute = entity.PrimaryIdAttribute,
                PrimaryNameAttribute = entity.PrimaryNameAttribute,
                SelectFields = built.SelectFields,
                Columns = CrmTableColumnBuilder.Build(entity, built.SelectFields)
            };
        }

        var items = ParseRows(http.Body, entity);
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
            crmQuery.relatedFromEntity,
            crmQuery.relativeRequestUri,
            crmQuery.readableQuery,
            httpStatus = http.StatusCode,
            recordCount = items.Count,
            durationMs = crmSw.ElapsedMilliseconds
        });
        return new GenericCrmQueryResult
        {
            Entity = logical,
            Operation = CrmQueryOperation.List,
            RelativeRequestUri = built.RelativeRequestUri,
            AppliedFilterSummary = built.AppliedFilterSummary,
            SortSummary = built.SortSummary,
            Limit = built.Limit,
            Items = items,
            Message = items.Count == 0 ? "No records matched the query." : null,
            ProviderName = ProviderName,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            SelectFields = built.SelectFields,
            Columns = CrmTableColumnBuilder.Build(entity, built.SelectFields, items)
        };
    }

    private static CrmQueryPlan CloneWithEntity(CrmQueryPlan plan, string entity) =>
        new()
        {
            Entity = entity,
            Operation = plan.Operation,
            Select = plan.Select,
            Filters = plan.Filters,
            RelativeDate = plan.RelativeDate,
            Sort = plan.Sort,
            Limit = plan.Limit,
            RelatedFromEntity = plan.RelatedFromEntity,
            RelatedFromId = plan.RelatedFromId
        };

    private static GenericCrmQueryResult Fail(CrmQueryPlan plan, string message) =>
        new()
        {
            Entity = plan.Entity,
            Operation = plan.Operation,
            RelativeRequestUri = string.Empty,
            Message = message,
            ProviderName = "Dynamics365"
        };

    private static (int Count, bool LimitExceeded) ReadODataCountInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var count = 0;
        if (root.TryGetProperty("@odata.count", out var countProp) && countProp.TryGetInt32(out var c))
        {
            count = c;
        }
        else if (root.TryGetProperty("@Microsoft.Dynamics.CRM.totalrecordcount", out var totalProp) &&
                 totalProp.TryGetInt32(out var total))
        {
            count = total;
        }

        var exceeded = false;
        if (root.TryGetProperty("@Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded", out var exceededProp))
        {
            exceeded = exceededProp.ValueKind == JsonValueKind.True ||
                       (exceededProp.ValueKind == JsonValueKind.String &&
                        bool.TryParse(exceededProp.GetString(), out var b) && b);
        }

        return (count, exceeded);
    }

    private static List<IReadOnlyDictionary<string, object?>> ParseRows(string json, CrmEntityMetadata entity)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var values))
        {
            return [];
        }

        var list = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in values.EnumerateArray())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var formattedByField = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in item.EnumerateObject())
            {
                // Capture Dynamics formatted-value annotations when Prefer returned them.
                // Example: totalamount@OData.Community.Display.V1.FormattedValue
                var annotationField = TryGetFormattedValueAnnotationField(prop.Name);
                if (annotationField is not null)
                {
                    var formatted = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(formatted))
                    {
                        formattedByField[annotationField] = formatted!;
                    }

                    continue;
                }

                if (prop.Name.StartsWith('@'))
                {
                    continue;
                }

                row[prop.Name] = ConvertJson(prop.Value);

                // Attach option labels when possible (skip nulls — TryGetInt32 throws on Null).
                if (prop.Value.ValueKind == JsonValueKind.Number &&
                    entity.TryGetAttribute(prop.Name, out var attr) &&
                    attr.Kind is CrmAttributeKind.Status or CrmAttributeKind.State or CrmAttributeKind.Picklist &&
                    prop.Value.TryGetInt32(out var code) &&
                    attr.OptionSetByValue.TryGetValue(code, out var label))
                {
                    row[prop.Name + "_label"] = label;
                }
            }

            foreach (var kv in formattedByField)
            {
                row[kv.Key + "_formatted"] = kv.Value;
            }

            list.Add(row);
        }

        return list;
    }

    private static string? TryGetFormattedValueAnnotationField(string propertyName)
    {
        const string community = "@OData.Community.Display.V1.FormattedValue";
        const string microsoft = "@Microsoft.Dynamics.CRM.formattedvalue";
        if (propertyName.EndsWith(community, StringComparison.OrdinalIgnoreCase))
        {
            return propertyName[..^community.Length];
        }

        if (propertyName.EndsWith(microsoft, StringComparison.OrdinalIgnoreCase))
        {
            return propertyName[..^microsoft.Length];
        }

        return null;
    }

    private static object? ConvertJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var l) => l,
        JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.ToString()
    };
}
