using System.Globalization;
using System.Text.Json;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;

namespace CrmAiPoc.Api.Crm.FetchXml;

/// <summary>
/// Validates LLM-generated FetchXML and executes it via the existing Dynamics Web API client.
/// </summary>
public sealed class FetchXmlQueryExecutor : IFetchXmlQueryExecutor
{
    private readonly FetchXmlSafetyValidator _validator;
    private readonly DynamicsCrmHttp _http;
    private readonly FetchXmlAggregatePartitionRecovery _partitionRecovery;
    private readonly ILogger<FetchXmlQueryExecutor> _logger;

    public FetchXmlQueryExecutor(
        FetchXmlSafetyValidator validator,
        DynamicsCrmHttp http,
        FetchXmlAggregatePartitionRecovery partitionRecovery,
        ILogger<FetchXmlQueryExecutor> logger)
    {
        _validator = validator;
        _http = http;
        _partitionRecovery = partitionRecovery;
        _logger = logger;
    }

    public Task<FetchXmlValidationOutcome> ValidateAsync(
        FetchXmlToolRequest request,
        CancellationToken cancellationToken = default) =>
        _validator.ValidateAsync(request, cancellationToken);

    public async Task<GenericCrmQueryResult> ExecuteAsync(
        ValidatedFetchXmlQuery query,
        CancellationToken cancellationToken = default)
    {
        _http.EnsureLiveConfigured();
        _logger.LogInformation(
            "[FETCHXML] Executing validated plan Entity={Entity} Aggregate={Aggregate} UriLength={UriLength}",
            query.EntityLogicalName,
            query.IsAggregate,
            query.RelativeRequestUri.Length);

        var http = await _http.GetAsync(query.RelativeRequestUri, includeAnnotations: true, cancellationToken);
        if (!http.IsSuccess)
        {
            var firstError = DynamicsCrmErrorParser.Parse(http.Body);
            // Do not retry the identical FetchXML without annotations for AggregateQueryRecordLimit —
            // that only doubles Dynamics load. Route to deterministic partition recovery instead.
            if (query.IsAggregate && firstError.IsAggregateQueryRecordLimit)
            {
                return await RecoverOrFailAggregateLimitAsync(query, http.StatusCode, firstError, cancellationToken);
            }

            _logger.LogWarning(
                "FetchXML with annotations failed ({Status}); retrying without Prefer.",
                http.StatusCode);
            http = await _http.GetAsync(query.RelativeRequestUri, includeAnnotations: false, cancellationToken);
        }

        if (!http.IsSuccess)
        {
            var err = DynamicsCrmErrorParser.Parse(http.Body);
            if (query.IsAggregate && err.IsAggregateQueryRecordLimit)
            {
                return await RecoverOrFailAggregateLimitAsync(query, http.StatusCode, err, cancellationToken);
            }

            var classification = DynamicsFetchXmlErrorClassifier.Classify(http.StatusCode, err);
            _logger.LogWarning(
                "[FETCHXML DYNAMICS ERROR] Status={Status} Entity={Entity} Aggregate={Aggregate} " +
                "Code={Code} Recoverable={Recoverable} Message={Message} UriLength={UriLength}",
                http.StatusCode,
                query.EntityLogicalName,
                query.IsAggregate,
                err.Code,
                classification.IsRecoverable,
                err.Message,
                query.RelativeRequestUri.Length);
            return Fail(
                query,
                classification.UserMessage,
                dynamicsErrorCode: err.Code,
                httpStatus: http.StatusCode,
                isRecoverable: classification.IsRecoverable,
                sanitizedFeedback: classification.SanitizedFeedback,
                correctionErrorCode: classification.ErrorCode);
        }

        var rows = ParseRows(http.Body, query.Entity);

        if (query.IsCountOnly || (!query.IsAggregate && rows.Count == 1 && TryReadCount(rows[0], out var countOnly)))
        {
            // Single-row aggregate count without groupby.
            if (query.IsCountOnly && TryReadCount(rows.FirstOrDefault(), out var c))
            {
                var capped = c >= CrmQueryLimits.DynamicsFetchXmlAggregateCountCeiling;
                return new GenericCrmQueryResult
                {
                    Entity = query.EntityLogicalName,
                    Operation = CrmQueryOperation.Count,
                    RelativeRequestUri = "(fetchXml count)",
                    Count = c,
                    CountMayBeCapped = capped,
                    Limit = query.Top ?? 0,
                    ProviderName = "Dynamics365",
                    PrimaryIdAttribute = query.Entity.PrimaryIdAttribute,
                    PrimaryNameAttribute = query.Entity.PrimaryNameAttribute,
                    Columns = query.Columns,
                    ResultMode = query.ResultMode.ToString().ToLowerInvariant(),
                    Limitation = query.Limitation
                };
            }
        }

        if (query.IsAggregate)
        {
            return new GenericCrmQueryResult
            {
                Entity = query.EntityLogicalName,
                Operation = CrmQueryOperation.Aggregate,
                RelativeRequestUri = "(fetchXml aggregate)",
                Limit = rows.Count,
                Items = rows,
                ProviderName = "Dynamics365",
                PrimaryIdAttribute = query.Entity.PrimaryIdAttribute,
                PrimaryNameAttribute = query.Entity.PrimaryNameAttribute,
                SelectFields = query.ProjectedAliases,
                // Keep planned group-by identity columns for aggregates; do not invent unplanned GUID columns.
                Columns = InferColumnsFromRows(query.Columns, rows, query.Entity.PrimaryIdAttribute),
                ResultMode = query.ResultMode.ToString().ToLowerInvariant(),
                Limitation = query.Limitation
            };
        }

        var listColumns = ApplyListDisplayColumnPolicy(
            InferColumnsFromRows(query.Columns, rows, query.Entity.PrimaryIdAttribute),
            query.Entity.PrimaryIdAttribute);

        return new GenericCrmQueryResult
        {
            Entity = query.EntityLogicalName,
            Operation = CrmQueryOperation.List,
            RelativeRequestUri = "(fetchXml list)",
            Limit = query.Top ?? rows.Count,
            Items = rows,
            ProviderName = "Dynamics365",
            PrimaryIdAttribute = query.Entity.PrimaryIdAttribute,
            PrimaryNameAttribute = query.Entity.PrimaryNameAttribute,
            SelectFields = query.ProjectedAliases,
            Columns = listColumns,
            ResultMode = query.ResultMode.ToString().ToLowerInvariant(),
            Limitation = query.Limitation
        };
    }

    private async Task<GenericCrmQueryResult> RecoverOrFailAggregateLimitAsync(
        ValidatedFetchXmlQuery query,
        int httpStatus,
        DynamicsCrmErrorParser.ParsedError err,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "[FETCHXML DYNAMICS ERROR] Status={Status} Entity={Entity} Aggregate=true " +
            "Code={Code} Classification=AGGREGATE_RECORD_LIMIT_EXCEEDED AttemptingPartitionRecovery=true Message={Message}",
            httpStatus,
            query.EntityLogicalName,
            err.Code,
            err.Message);

        try
        {
            var recovered = await _partitionRecovery.TryRecoverAsync(query, cancellationToken);
            if (recovered is not null &&
                !string.IsNullOrWhiteSpace(recovered.RelativeRequestUri))
            {
                _logger.LogInformation(
                    "[AGGREGATE PARTITION] Recovery succeeded Entity={Entity} Rows={Rows}",
                    query.EntityLogicalName,
                    recovered.Items.Count);
                return recovered;
            }

            if (recovered is not null)
            {
                // Explicit limitation from recovery (non-recoverable for Groq).
                return recovered;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AGGREGATE PARTITION] Recovery threw for Entity={Entity}", query.EntityLogicalName);
        }

        var classification = DynamicsFetchXmlErrorClassifier.Classify(httpStatus, err);
        return Fail(
            query,
            classification.UserMessage,
            dynamicsErrorCode: err.Code,
            httpStatus: httpStatus,
            isRecoverable: false,
            sanitizedFeedback: classification.SanitizedFeedback,
            correctionErrorCode: "AGGREGATE_RECORD_LIMIT_EXCEEDED");
    }

    private static GenericCrmQueryResult Fail(
        ValidatedFetchXmlQuery query,
        string message,
        string? dynamicsErrorCode = null,
        int? httpStatus = null,
        bool isRecoverable = false,
        string? sanitizedFeedback = null,
        string? correctionErrorCode = null) =>
        new()
        {
            Entity = query.EntityLogicalName,
            Operation = query.IsAggregate ? CrmQueryOperation.Aggregate : CrmQueryOperation.List,
            RelativeRequestUri = string.Empty,
            Message = message,
            DynamicsErrorCode = dynamicsErrorCode,
            DynamicsHttpStatus = httpStatus,
            IsRecoverableFetchXmlError = isRecoverable,
            SanitizedCorrectionFeedback = sanitizedFeedback,
            CorrectionErrorCode = correctionErrorCode,
            ProviderName = "Dynamics365",
            ResultMode = query.ResultMode.ToString().ToLowerInvariant(),
            Limitation = query.Limitation
        };

    private static bool TryReadCount(IReadOnlyDictionary<string, object?>? row, out int count)
    {
        count = 0;
        if (row is null)
        {
            return false;
        }

        foreach (var kv in row)
        {
            if (kv.Value is null)
            {
                continue;
            }

            if (kv.Value is int i)
            {
                count = i;
                return true;
            }

            if (kv.Value is long l)
            {
                count = (int)l;
                return true;
            }

            if (kv.Value is decimal d)
            {
                count = (int)d;
                return true;
            }

            if (kv.Value is string s &&
                int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
            {
                count = p;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Prefer planned metadata columns (labels/kinds from FetchXML + CRM metadata).
    /// Append extra row keys so link-entity aliases are never dropped after recovery.
    /// Does not invent user-facing columns for unplanned primary IDs or raw GUID-only keys;
    /// those values remain on the row dictionaries for identity/navigation.
    /// </summary>
    internal static IReadOnlyList<CrmTableColumn> InferColumnsFromRows(
        IReadOnlyList<CrmTableColumn> planned,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string? primaryIdAttribute = null)
    {
        var result = new List<CrmTableColumn>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in planned)
        {
            if (string.IsNullOrWhiteSpace(col.Key) || !seen.Add(col.Key))
            {
                continue;
            }

            result.Add(col);
        }

        if (rows.Count == 0)
        {
            return result;
        }

        foreach (var key in rows[0].Keys)
        {
            if (string.IsNullOrWhiteSpace(key) ||
                key.StartsWith('@') ||
                key.Contains("@OData.", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("@Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                key.EndsWith("_formatted", StringComparison.OrdinalIgnoreCase) ||
                key.EndsWith("_label", StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(key))
            {
                continue;
            }

            if (IsUnplannedIdentityKey(key, primaryIdAttribute, rows))
            {
                continue;
            }

            result.Add(new CrmTableColumn
            {
                Key = key,
                Label = key,
                Kind = "string",
                LogicalName = key
            });
        }

        return result;
    }

    /// <summary>
    /// List-table display policy: keep identity GUIDs on records, but do not expose
    /// uniqueidentifier / primary-id columns as empty or raw GUID table headers by default.
    /// Aggregate group-by identity columns are left intact by the caller.
    /// </summary>
    internal static IReadOnlyList<CrmTableColumn> ApplyListDisplayColumnPolicy(
        IReadOnlyList<CrmTableColumn> columns,
        string? primaryIdAttribute)
    {
        if (columns.Count == 0)
        {
            return columns;
        }

        var filtered = new List<CrmTableColumn>(columns.Count);
        foreach (var col in columns)
        {
            if (IsIdentityDisplayColumn(col, primaryIdAttribute))
            {
                continue;
            }

            filtered.Add(col);
        }

        return filtered;
    }

    internal static bool IsIdentityDisplayColumn(CrmTableColumn column, string? primaryIdAttribute)
    {
        var kind = column.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
        if (kind is "uniqueidentifier" or "guid")
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(primaryIdAttribute))
        {
            return false;
        }

        return column.Key.Equals(primaryIdAttribute, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(column.LogicalName) &&
                column.LogicalName.Equals(primaryIdAttribute, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Unplanned keys that are the root primary id or only contain GUID samples (no display text)
    /// are internal identity / raw lookup values — keep on rows, omit from inferred display columns.
    /// </summary>
    private static bool IsUnplannedIdentityKey(
        string key,
        string? primaryIdAttribute,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (!string.IsNullOrWhiteSpace(primaryIdAttribute) &&
            key.Equals(primaryIdAttribute, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!AllNonNullSamplesGuidLike(key, rows))
        {
            return false;
        }

        // Raw GUID column without a formatted companion is not a useful display field.
        var formattedKey = key + "_formatted";
        foreach (var row in rows)
        {
            if (row.TryGetValue(formattedKey, out var formatted) &&
                formatted is string s &&
                !string.IsNullOrWhiteSpace(s))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllNonNullSamplesGuidLike(
        string key,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var sawValue = false;
        foreach (var row in rows)
        {
            if (!row.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            var text = value.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            sawValue = true;
            if (!Guid.TryParse(text.Trim(), out _))
            {
                return false;
            }
        }

        return sawValue;
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
            foreach (var prop in item.EnumerateObject())
            {
                if (prop.Name.StartsWith('@') ||
                    prop.Name.Contains("@OData.", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Contains("@Microsoft.", StringComparison.OrdinalIgnoreCase))
                {
                    var formattedField = TryGetFormattedValueAnnotationField(prop.Name);
                    if (formattedField is not null && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        row[formattedField + "_formatted"] = prop.Value.GetString();
                    }

                    continue;
                }

                row[prop.Name] = ConvertJson(prop.Value);

                if (prop.Value.ValueKind == JsonValueKind.Number &&
                    entity.TryGetAttribute(prop.Name, out var attr) &&
                    attr.Kind is CrmAttributeKind.Status or CrmAttributeKind.State or CrmAttributeKind.Picklist &&
                    prop.Value.TryGetInt32(out var code) &&
                    attr.OptionSetByValue.TryGetValue(code, out var label))
                {
                    row[prop.Name + "_label"] = label;
                }
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
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var l) => l,
        JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
        JsonValueKind.Number => value.GetDouble(),
        _ => value.ToString()
    };
}
