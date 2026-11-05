using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Read-only Dynamics 365 On-Prem reporting-entity discovery.
// Does not modify CRM, ApprovedEntities, or send records to AI providers.

var repoRoot = FindRepoRoot();
var appsettingsPath = Path.Combine(repoRoot, "src", "CrmAiPoc.Api", "appsettings.Development.json");
if (!File.Exists(appsettingsPath))
{
    Console.Error.WriteLine($"Missing {appsettingsPath}");
    return 1;
}

using var cfgDoc = JsonDocument.Parse(await File.ReadAllTextAsync(appsettingsPath));
var crm = cfgDoc.RootElement.GetProperty("Crm");
var baseUrl = crm.GetProperty("BaseUrl").GetString()?.TrimEnd('/') + "/"
              ?? throw new InvalidOperationException("Crm:BaseUrl missing");
var approved = crm.GetProperty("ApprovedEntities").EnumerateArray()
    .Select(e => e.GetString()!)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

Console.WriteLine($"BaseUrl host: {new Uri(baseUrl).Host}");
Console.WriteLine($"Current ApprovedEntities (unchanged): {string.Join(", ", approved.OrderBy(x => x))}");

using var handler = new HttpClientHandler
{
    UseDefaultCredentials = true,
    Credentials = CredentialCache.DefaultCredentials,
    PreAuthenticate = true,
    CheckCertificateRevocationList = true
};
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
http.DefaultRequestHeaders.TryAddWithoutValidation("OData-MaxVersion", "4.0");
http.DefaultRequestHeaders.TryAddWithoutValidation("OData-Version", "4.0");

var who = await GetJsonAsync(http, baseUrl, "WhoAmI()");
if (who is null)
{
    Console.Error.WriteLine("WhoAmI failed — cannot continue discovery.");
    return 2;
}

Console.WriteLine("WhoAmI succeeded.");

// ---------- Phase 1: Entity inventory ----------
Console.WriteLine("Phase 1: EntityDefinitions inventory…");
var entitySelect =
    "LogicalName,SchemaName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute," +
    "IsCustomEntity,OwnershipType,IsValidForAdvancedFind,ObjectTypeCode,Description,DisplayName";
var entityFilter = "IsIntersect eq false";
var entityRows = await GetAllODataAsync(
    http,
    baseUrl,
    $"EntityDefinitions?$select={entitySelect}&$filter={Uri.EscapeDataString(entityFilter)}");

var entities = new List<EntityInventoryRow>();
var objectTypeToLogical = new Dictionary<int, string>();
foreach (var row in entityRows)
{
    var logical = GetString(row, "LogicalName");
    if (string.IsNullOrWhiteSpace(logical))
    {
        continue;
    }

    var objectTypeCode = GetInt(row, "ObjectTypeCode");
    if (objectTypeCode is int otc)
    {
        objectTypeToLogical[otc] = logical;
    }

    entities.Add(new EntityInventoryRow
    {
        LogicalName = logical,
        SchemaName = GetString(row, "SchemaName"),
        DisplayName = GetLocalized(row, "DisplayName") ?? logical,
        Description = GetLocalized(row, "Description"),
        EntitySetName = GetString(row, "EntitySetName"),
        PrimaryIdAttribute = GetString(row, "PrimaryIdAttribute"),
        PrimaryNameAttribute = GetString(row, "PrimaryNameAttribute"),
        IsCustomEntity = GetBool(row, "IsCustomEntity") ?? false,
        OwnershipType = MapOwnership(GetInt(row, "OwnershipType"), GetString(row, "OwnershipType")),
        IsValidForAdvancedFind = GetBool(row, "IsValidForAdvancedFind"),
        ObjectTypeCode = objectTypeCode,
        IsCurrentlyApproved = approved.Contains(logical),
        IsTeOrTedCustom = logical.StartsWith("te_", StringComparison.OrdinalIgnoreCase)
                          || logical.StartsWith("ted_", StringComparison.OrdinalIgnoreCase)
    });
}

Console.WriteLine($"Discovered {entities.Count} non-intersect entities. Collecting attribute/relationship counts…");
await PopulateCountsAsync(http, baseUrl, entities);

var inventoryPath = Path.Combine(repoRoot, "crm-entity-inventory.json");
var inventoryPayload = new
{
    generatedAtUtc = DateTimeOffset.UtcNow,
    organizationHint = "ECRMBSS / Dynamics 365 On-Premises 9.1",
    source = "EntityDefinitions (read-only Web API)",
    baseUrlHost = new Uri(baseUrl).Host,
    currentlyApprovedEntities = approved.OrderBy(x => x).ToArray(),
    notes = new[]
    {
        "Discovery only — ApprovedEntities were not modified.",
        "IsIntersect=false filter applied to exclude N:N intersect entities.",
        "Attribute/relationship counts are live /$count requests; null means count unavailable or denied.",
        "No CRM business records were retrieved."
    },
    entityCount = entities.Count,
    entities = entities.OrderBy(e => e.LogicalName, StringComparer.OrdinalIgnoreCase).ToList()
};
await WriteJsonAsync(inventoryPath, inventoryPayload);
Console.WriteLine($"Wrote {inventoryPath}");

// ---------- Phase 2: Reports and views ----------
Console.WriteLine("Phase 2: Reports / savedquery / userquery…");
var reportNotes = new List<string>();
var reports = new List<ReportRow>();
var reportEntities = new List<ReportEntityRef>();
var systemViews = new List<ViewRow>();
var userViews = new List<ViewRow>();
var reportCategories = new List<object>();

var reportRows = await TryGetAllODataAsync(
    http,
    baseUrl,
    "reports?$select=reportid,name,description,filename,reporttypecode,iscustomreport,ispersonal,languagecode,createdon,modifiedon&$orderby=name",
    reportNotes,
    "reports");
foreach (var r in reportRows)
{
    reports.Add(new ReportRow
    {
        ReportId = GetGuid(r, "reportid"),
        Name = GetString(r, "name"),
        Description = Truncate(GetString(r, "description"), 500),
        FileName = GetString(r, "filename"),
        ReportTypeCode = GetInt(r, "reporttypecode"),
        IsCustomReport = GetBool(r, "iscustomreport"),
        IsPersonal = GetBool(r, "ispersonal"),
        LanguageCode = GetInt(r, "languagecode"),
        CreatedOn = GetString(r, "createdon"),
        ModifiedOn = GetString(r, "modifiedon")
    });
}

var reportEntityRows = await TryGetAllODataAsync(
    http,
    baseUrl,
    "reportentities?$select=reportentityid,reportid,objecttypecode",
    reportNotes,
    "reportentities");
if (reportEntityRows.Count > 0)
{
    Console.WriteLine("Sample reportentity properties: " +
                      string.Join(", ", reportEntityRows[0].EnumerateObject().Select(p => $"{p.Name}={Truncate(p.Value.ToString(), 40)}")));
}

foreach (var re in reportEntityRows)
{
    // On-Prem may return objecttypecode as int type-code OR as entity logical name string.
    var otc = GetInt(re, "objecttypecode") ?? GetInt(re, "ObjectTypeCode");
    var otcRaw = GetString(re, "objecttypecode") ?? GetString(re, "ObjectTypeCode") ?? GetString(re, "name");
    string? logical = null;
    if (otc is int code)
    {
        objectTypeToLogical.TryGetValue(code, out logical);
    }

    if (logical is null && !string.IsNullOrWhiteSpace(otcRaw))
    {
        if (int.TryParse(otcRaw, out var parsedCode))
        {
            objectTypeToLogical.TryGetValue(parsedCode, out logical);
            otc ??= parsedCode;
        }
        else if (entities.Any(e => e.LogicalName.Equals(otcRaw, StringComparison.OrdinalIgnoreCase)))
        {
            logical = otcRaw;
        }
    }

    reportEntities.Add(new ReportEntityRef
    {
        ReportId = GetGuid(re, "reportid") ?? GetGuid(re, "ReportId"),
        ObjectTypeCode = otc,
        EntityLogicalName = logical,
        EvidenceKind = logical is null
            ? "objecttypecode_unmapped"
            : (otc is null ? "reportentities_logicalname_string" : "reportentities_objecttypecode_mapped")
    });
}

// Categories (best-effort)
var categoryRows = await TryGetAllODataAsync(
    http,
    baseUrl,
    "reportcategories?$select=reportcategoryid,reportid,categorycode",
    reportNotes,
    "reportcategories");
foreach (var c in categoryRows)
{
    reportCategories.Add(new
    {
        reportCategoryId = GetGuid(c, "reportcategoryid"),
        reportId = GetGuid(c, "reportid"),
        categoryCode = GetInt(c, "categorycode")
    });
}

var savedRows = await TryGetAllODataAsync(
    http,
    baseUrl,
    "savedqueries?$select=savedqueryid,name,description,returnedtypecode,querytype,isquickfindquery,isdefault,isprivate,createdon,modifiedon&$filter=querytype eq 0&$orderby=name",
    reportNotes,
    "savedqueries (querytype=0 public views)");
foreach (var v in savedRows)
{
    systemViews.Add(new ViewRow
    {
        ViewId = GetGuid(v, "savedqueryid"),
        Name = GetString(v, "name"),
        Description = Truncate(GetString(v, "description"), 400),
        ReturnedTypeCode = GetString(v, "returnedtypecode"),
        QueryType = GetInt(v, "querytype"),
        IsQuickFind = GetBool(v, "isquickfindquery"),
        IsDefault = GetBool(v, "isdefault"),
        IsPrivate = GetBool(v, "isprivate"),
        CreatedOn = GetString(v, "createdon"),
        ModifiedOn = GetString(v, "modifiedon"),
        Source = "savedquery"
    });
}

var userRows = await TryGetAllODataAsync(
    http,
    baseUrl,
    "userqueries?$select=userqueryid,name,description,returnedtypecode,querytype,createdon,modifiedon&$orderby=name",
    reportNotes,
    "userqueries");
foreach (var v in userRows)
{
    userViews.Add(new ViewRow
    {
        ViewId = GetGuid(v, "userqueryid"),
        Name = GetString(v, "name"),
        Description = Truncate(GetString(v, "description"), 400),
        ReturnedTypeCode = GetString(v, "returnedtypecode"),
        QueryType = GetInt(v, "querytype"),
        CreatedOn = GetString(v, "createdon"),
        ModifiedOn = GetString(v, "modifiedon"),
        Source = "userquery"
    });
}

reportNotes.Add("Report usage/frequency statistics are not available via these metadata endpoints; no popularity invented.");
reportNotes.Add("Report binaries / fetchxml datasets / filter values were not retrieved.");
reportNotes.Add("Observed: reportentities.objecttypecode is an entity logical name string on this org (not a numeric type code).");
reportNotes.Add("Observed: reportentities rows often omit reportid in Web API payloads; hit counts use link-row counts per entity.");
reportNotes.Add("View ReturnedTypeCode is treated as entity logical name when it matches an EntityDefinitions LogicalName.");

// On-Prem reportentities often omit reportid in $select payloads; count link rows per entity logical name.
var entityReportHits = reportEntities
    .Where(x => !string.IsNullOrWhiteSpace(x.EntityLogicalName))
    .GroupBy(x => x.EntityLogicalName!, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(
        g => g.Key,
        g =>
        {
            var withIds = g.Select(x => x.ReportId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().Count();
            return withIds > 0 ? withIds : g.Count();
        },
        StringComparer.OrdinalIgnoreCase);

var entityViewHits = systemViews.Concat(userViews)
    .Where(v => !string.IsNullOrWhiteSpace(v.ReturnedTypeCode))
    .GroupBy(v => v.ReturnedTypeCode!, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

var reportInventoryPath = Path.Combine(repoRoot, "crm-report-inventory.json");
var reportInventoryPayload = new
{
    generatedAtUtc = DateTimeOffset.UtcNow,
    source = "reports, reportentities, reportcategories, savedqueries, userqueries (read-only)",
    notes = reportNotes,
    counts = new
    {
        reports = reports.Count,
        reportEntityLinks = reportEntities.Count,
        reportCategories = reportCategories.Count,
        systemViews = systemViews.Count,
        userViews = userViews.Count
    },
    reports,
    reportEntityReferences = reportEntities,
    reportCategories,
    systemViews,
    userViews,
    entityHitSummary = new
    {
        fromReports = entityReportHits.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Select(kv => new { entity = kv.Key, reportEntityLinkCount = kv.Value }).ToList(),
        fromViews = entityViewHits.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Select(kv => new { entity = kv.Key, viewCount = kv.Value }).ToList()
    }
};
await WriteJsonAsync(reportInventoryPath, reportInventoryPayload);
Console.WriteLine($"Wrote {reportInventoryPath}");

// ---------- Phase 3–5: Rank + detail + recommendations ----------
Console.WriteLine("Phases 3–5: Ranking candidates and collecting detailed metadata…");
var namedCandidates = new[]
{
    "salesorderdetail", "opportunity", "quote", "invoice", "product", "systemuser", "campaign",
    "activitypointer", "quotedetail", "invoicedetail", "opportunityproduct", "businessunit",
    "phonecall", "email", "task", "appointment", "campaignresponse", "campaignactivity",
    "list", "competitor", "pricelevel", "uom", "uomschedule", "transactioncurrency",
    "incident", "contract", "entitlement", "goal", "metric"
};

var ranked = RankEntities(entities, approved, entityReportHits, entityViewHits, namedCandidates);
var top = ranked.Take(20).ToList();

var details = new ConcurrentDictionary<string, EntityDetail>(StringComparer.OrdinalIgnoreCase);
await Parallel.ForEachAsync(top, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (cand, ct) =>
{
    var detail = await LoadEntityDetailAsync(http, baseUrl, cand.Entity.LogicalName, approved, ct);
    details[cand.Entity.LogicalName] = detail;
});

var recommendationsPath = Path.Combine(repoRoot, "crm-report-entity-recommendations.md");
await File.WriteAllTextAsync(recommendationsPath, BuildMarkdown(
    entities.Count,
    reports.Count,
    systemViews.Count,
    userViews.Count,
    approved,
    ranked,
    top,
    details,
    entityReportHits,
    entityViewHits,
    reportNotes));
Console.WriteLine($"Wrote {recommendationsPath}");
Console.WriteLine("Discovery complete. ApprovedEntities were not changed.");
return 0;

// ================= helpers =================

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "CrmAiPoc.Api", "CrmAiPoc.Api.csproj")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return Directory.GetCurrentDirectory();
}

static async Task WriteJsonAsync(string path, object payload)
{
    var opts = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, opts));
}

static async Task<JsonElement?> GetJsonAsync(HttpClient http, string baseUrl, string relative)
{
    var result = await SendAsync(http, baseUrl, relative);
    if (!result.Success)
    {
        Console.WriteLine($"GET failed {result.Status} {relative}: {Truncate(result.Body, 200)}");
        return null;
    }

    using var doc = JsonDocument.Parse(result.Body);
    return doc.RootElement.Clone();
}

static async Task<(bool Success, int Status, string Body)> SendAsync(HttpClient http, string baseUrl, string relative)
{
    var url = baseUrl.TrimEnd('/') + "/" + relative.TrimStart('/');
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    using var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
}

static async Task<List<JsonElement>> GetAllODataAsync(HttpClient http, string baseUrl, string relativeOrAbsolute)
{
    var list = new List<JsonElement>();
    string? next = relativeOrAbsolute.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        ? relativeOrAbsolute
        : baseUrl.TrimEnd('/') + "/" + relativeOrAbsolute.TrimStart('/');

    while (!string.IsNullOrWhiteSpace(next))
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, next);
        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OData GET failed {(int)resp.StatusCode}: {Truncate(body, 300)} ({next})");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("value", out var values))
        {
            foreach (var item in values.EnumerateArray())
            {
                list.Add(item.Clone());
            }
        }
        else
        {
            list.Add(doc.RootElement.Clone());
            break;
        }

        next = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl)
            ? nl.GetString()
            : null;
    }

    return list;
}

static async Task<List<JsonElement>> TryGetAllODataAsync(
    HttpClient http,
    string baseUrl,
    string relative,
    List<string> notes,
    string label)
{
    try
    {
        var rows = await GetAllODataAsync(http, baseUrl, relative);
        notes.Add($"{label}: retrieved {rows.Count} row(s).");
        return rows;
    }
    catch (Exception ex)
    {
        notes.Add($"{label}: unavailable ({ex.Message}).");
        Console.WriteLine($"Skip {label}: {ex.Message}");
        return [];
    }
}

static async Task PopulateCountsAsync(HttpClient http, string baseUrl, List<EntityInventoryRow> entities)
{
    var gate = new SemaphoreSlim(8);
    var done = 0;
    await Parallel.ForEachAsync(entities, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (e, ct) =>
    {
        await gate.WaitAsync(ct);
        try
        {
            e.AttributesCount = await TryCountAsync(http, baseUrl,
                $"EntityDefinitions(LogicalName='{EscapeOData(e.LogicalName)}')/Attributes/$count", ct);
            var m2o = await TryCountAsync(http, baseUrl,
                $"EntityDefinitions(LogicalName='{EscapeOData(e.LogicalName)}')/ManyToOneRelationships/$count", ct);
            var o2m = await TryCountAsync(http, baseUrl,
                $"EntityDefinitions(LogicalName='{EscapeOData(e.LogicalName)}')/OneToManyRelationships/$count", ct);
            if (m2o is null && o2m is null)
            {
                e.RelationshipsCount = null;
            }
            else
            {
                e.RelationshipsCount = (m2o ?? 0) + (o2m ?? 0);
            }
        }
        finally
        {
            gate.Release();
            var n = Interlocked.Increment(ref done);
            if (n % 50 == 0 || n == entities.Count)
            {
                Console.WriteLine($"  counts {n}/{entities.Count}");
            }
        }
    });
}

static async Task<int?> TryCountAsync(HttpClient http, string baseUrl, string relative, CancellationToken ct)
{
    try
    {
        var url = baseUrl.TrimEnd('/') + "/" + relative.TrimStart('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        req.Headers.TryAddWithoutValidation("OData-MaxVersion", "4.0");
        req.Headers.TryAddWithoutValidation("OData-Version", "4.0");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var text = (await resp.Content.ReadAsStringAsync(ct)).Trim();
        return int.TryParse(text, out var n) ? n : null;
    }
    catch
    {
        return null;
    }
}

static List<RankedCandidate> RankEntities(
    List<EntityInventoryRow> entities,
    HashSet<string> approved,
    Dictionary<string, int> reportHits,
    Dictionary<string, int> viewHits,
    string[] namedCandidates)
{
    var named = namedCandidates.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var salesMarketingHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "opportunity", "quote", "invoice", "salesorder", "salesorderdetail", "product", "campaign",
        "lead", "account", "contact", "quotedetail", "invoicedetail", "opportunityproduct",
        "campaignresponse", "campaignactivity", "list", "competitor", "pricelevel", "systemuser",
        "businessunit", "activitypointer", "phonecall", "email", "task", "appointment"
    };

    var ranked = entities.Select(e =>
    {
        reportHits.TryGetValue(e.LogicalName, out var rh);
        viewHits.TryGetValue(e.LogicalName, out var vh);
        var score = 0.0;
        score += Math.Min(rh, 25) * 4.0;          // reports evidence
        score += Math.Min(vh, 40) * 1.5;          // views evidence
        if (named.Contains(e.LogicalName)) score += 35;
        if (salesMarketingHints.Contains(e.LogicalName)) score += 20;
        if (e.IsCurrentlyApproved) score += 15;
        if (e.IsTeOrTedCustom) score += 8;
        if (e.IsCustomEntity && (e.IsValidForAdvancedFind ?? false)) score += 6;
        if (e.IsValidForAdvancedFind == false) score -= 10;
        // Soft penalty for pure config / internal tables by name patterns
        if (LooksLikeInternal(e.LogicalName)) score -= 25;
        return new RankedCandidate
        {
            Entity = e,
            Score = score,
            ReportDistinctCount = rh,
            ViewCount = vh,
            NamedCandidate = named.Contains(e.LogicalName),
            SalesMarketingHint = salesMarketingHints.Contains(e.LogicalName)
        };
    })
    .OrderByDescending(c => c.Score)
    .ThenByDescending(c => c.ReportDistinctCount)
    .ThenByDescending(c => c.ViewCount)
    .ThenBy(c => c.Entity.LogicalName, StringComparer.OrdinalIgnoreCase)
    .ToList();

    return ranked;
}

static bool LooksLikeInternal(string logical) =>
    logical.StartsWith("msdyn_", StringComparison.OrdinalIgnoreCase)
    || logical.StartsWith("msfp_", StringComparison.OrdinalIgnoreCase)
    || logical.StartsWith("adx_", StringComparison.OrdinalIgnoreCase)
    || logical.Contains("import", StringComparison.OrdinalIgnoreCase)
    || logical.Contains("plugin", StringComparison.OrdinalIgnoreCase)
    || logical.Contains("workflow", StringComparison.OrdinalIgnoreCase)
    || logical.Contains("asyncoperation", StringComparison.OrdinalIgnoreCase)
    || logical.Contains("principal", StringComparison.OrdinalIgnoreCase)
    || logical.EndsWith("map", StringComparison.OrdinalIgnoreCase)
        && logical.Contains("attribute", StringComparison.OrdinalIgnoreCase);

static async Task<EntityDetail> LoadEntityDetailAsync(
    HttpClient http,
    string baseUrl,
    string logicalName,
    HashSet<string> approved,
    CancellationToken ct)
{
    var detail = new EntityDetail { LogicalName = logicalName };
    try
    {
        var attrsPath =
            $"EntityDefinitions(LogicalName='{EscapeOData(logicalName)}')/Attributes" +
            "?$select=LogicalName,SchemaName,AttributeType,DisplayName,IsValidForRead,IsValidForForm,IsValidForGrid,IsCustomAttribute,RequiredLevel" +
            "&$filter=IsValidForRead eq true";
        var attrs = await GetAllODataAsync(http, baseUrl, attrsPath);
        foreach (var a in attrs)
        {
            var type = GetString(a, "AttributeType") ?? "";
            detail.Attributes.Add(new AttributeDetail
            {
                LogicalName = GetString(a, "LogicalName"),
                DisplayName = GetLocalized(a, "DisplayName"),
                AttributeType = type,
                IsValidForRead = GetBool(a, "IsValidForRead"),
                IsValidForGrid = GetBool(a, "IsValidForGrid"),
                IsCustomAttribute = GetBool(a, "IsCustomAttribute"),
                UsefulForReporting = IsUsefulReportingType(type)
            });
        }

        // Lookup targets (sample Picklist/Lookup attributes)
        var lookupPath =
            $"EntityDefinitions(LogicalName='{EscapeOData(logicalName)}')/Attributes/Microsoft.Dynamics.CRM.LookupAttributeMetadata" +
            "?$select=LogicalName,Targets,DisplayName";
        try
        {
            var lookups = await GetAllODataAsync(http, baseUrl, lookupPath);
            foreach (var l in lookups)
            {
                var name = GetString(l, "LogicalName");
                var targets = l.TryGetProperty("Targets", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : Array.Empty<string>();
                var existing = detail.Attributes.FirstOrDefault(x =>
                    string.Equals(x.LogicalName, name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.LookupTargets = targets;
                }
            }
        }
        catch
        {
            // optional
        }

        // Option sets (limited)
        try
        {
            var optionPath =
                $"EntityDefinitions(LogicalName='{EscapeOData(logicalName)}')/Attributes/Microsoft.Dynamics.CRM.PicklistAttributeMetadata" +
                "?$select=LogicalName,DisplayName&$expand=OptionSet($select=Options)";
            var options = await GetAllODataAsync(http, baseUrl, optionPath);
            foreach (var o in options.Take(25))
            {
                var name = GetString(o, "LogicalName");
                var labels = new List<string>();
                if (o.TryGetProperty("OptionSet", out var os) &&
                    os.TryGetProperty("Options", out var opts) &&
                    opts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var opt in opts.EnumerateArray().Take(20))
                    {
                        var value = GetInt(opt, "Value");
                        var label = GetLocalized(opt, "Label");
                        if (value is not null)
                        {
                            labels.Add($"{value}:{label}");
                        }
                    }
                }

                var existing = detail.Attributes.FirstOrDefault(x =>
                    string.Equals(x.LogicalName, name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.OptionSetSummary = labels;
                }
            }
        }
        catch
        {
            // optional
        }

        async Task AddRels(string path, string kind)
        {
            try
            {
                var rows = await GetAllODataAsync(http, baseUrl, path);
                foreach (var r in rows)
                {
                    var referencing = GetString(r, "ReferencingEntity");
                    var referenced = GetString(r, "ReferencedEntity");
                    var touchesApproved =
                        (referencing is not null && approved.Contains(referencing)) ||
                        (referenced is not null && approved.Contains(referenced)) ||
                        string.Equals(referencing, logicalName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(referenced, logicalName, StringComparison.OrdinalIgnoreCase);

                    // Keep relationships involving approved entities or self; also keep first N overall.
                    detail.Relationships.Add(new RelationshipDetail
                    {
                        SchemaName = GetString(r, "SchemaName"),
                        Kind = kind,
                        ReferencingEntity = referencing,
                        ReferencedEntity = referenced,
                        ReferencingAttribute = GetString(r, "ReferencingAttribute"),
                        ReferencedAttribute = GetString(r, "ReferencedAttribute"),
                        TouchesApprovedEntity = touchesApproved,
                        MayDuplicateRootViaOneToMany = string.Equals(kind, "OneToMany", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex)
            {
                detail.Notes.Add($"{kind} relationships unavailable: {ex.Message}");
            }
        }

        await AddRels(
            $"EntityDefinitions(LogicalName='{EscapeOData(logicalName)}')/ManyToOneRelationships" +
            "?$select=SchemaName,ReferencingEntity,ReferencedEntity,ReferencingAttribute,ReferencedAttribute",
            "ManyToOne");
        await AddRels(
            $"EntityDefinitions(LogicalName='{EscapeOData(logicalName)}')/OneToManyRelationships" +
            "?$select=SchemaName,ReferencingEntity,ReferencedEntity,ReferencingAttribute,ReferencedAttribute",
            "OneToMany");

        // Keep relationships manageable: prefer approved-touching, then cap.
        detail.Relationships = detail.Relationships
            .OrderByDescending(r => r.TouchesApprovedEntity)
            .ThenBy(r => r.SchemaName)
            .Take(80)
            .ToList();
    }
    catch (Exception ex)
    {
        detail.Notes.Add($"Detail load failed: {ex.Message}");
    }

    return detail;
}

static bool IsUsefulReportingType(string type)
{
    var t = type.ToLowerInvariant();
    return t is "money" or "decimal" or "double" or "integer" or "bigint"
        or "datetime" or "owner" or "customer" or "lookup" or "picklist"
        or "state" or "status" or "boolean" or "string" or "memo";
}

static string BuildMarkdown(
    int entityCount,
    int reportCount,
    int systemViewCount,
    int userViewCount,
    HashSet<string> approved,
    List<RankedCandidate> ranked,
    List<RankedCandidate> top,
    ConcurrentDictionary<string, EntityDetail> details,
    Dictionary<string, int> reportHits,
    Dictionary<string, int> viewHits,
    List<string> reportNotes)
{
    var sb = new StringBuilder();
    sb.AppendLine("# CRM Report Entity Recommendations");
    sb.AppendLine();
    sb.AppendLine($"Generated (UTC): {DateTimeOffset.UtcNow:u}");
    sb.AppendLine();
    sb.AppendLine("## Scope");
    sb.AppendLine();
    sb.AppendLine("- **Discovery only** — `ApprovedEntities` was **not** modified.");
    sb.AppendLine("- Organization context: ECRMBSS / Dynamics 365 On-Premises 9.1 (live Web API read-only).");
    sb.AppendLine($"- Currently approved: `{string.Join("`, `", approved.OrderBy(x => x))}`.");
    sb.AppendLine($"- EntityDefinitions inventoried: **{entityCount}** (IsIntersect=false).");
    sb.AppendLine($"- Reports retrieved: **{reportCount}**; system views: **{systemViewCount}**; user views: **{userViewCount}**.");
    sb.AppendLine("- Report usage frequency: **not available** via these endpoints (not invented).");
    sb.AppendLine("- No CRM business records or report datasets were exported.");
    sb.AppendLine();
    sb.AppendLine("## Evidence vs recommendation");
    sb.AppendLine();
    sb.AppendLine("| Kind | Meaning |");
    sb.AppendLine("|------|---------|");
    sb.AppendLine("| **Observed** | Present in live EntityDefinitions / reports / views metadata |");
    sb.AppendLine("| **Recommended** | Analyst ranking for Sales/Marketing/Management reporting expansion |");
    sb.AppendLine();
    sb.AppendLine("### Report/view retrieval notes (observed)");
    sb.AppendLine();
    foreach (var n in reportNotes.Take(20))
    {
        sb.AppendLine($"- {n}");
    }

    sb.AppendLine();
    sb.AppendLine("## Current query engine constraints (observed in code)");
    sb.AppendLine();
    sb.AppendLine("- Supported: list, count, aggregate, groupBy, date ranges, sort, filters (max 3), single-hop and two-hop `linksJson` (max depth 2 / max 3 links).");
    sb.AppendLine("- **Unsupported today: `operation=aggregate` + `linksJson`** (parser/builder reject). Mark such questions as unsupported until implemented.");
    sb.AppendLine("- Aggregate root-only; detail lines (e.g. salesorderdetail) can aggregate on themselves, not as aggregate-with-parent-link in one call.");
    sb.AppendLine();
    sb.AppendLine("## Top recommended entities");
    sb.AppendLine();

    var phase = 1;
    for (var i = 0; i < top.Count; i++)
    {
        var c = top[i];
        details.TryGetValue(c.Entity.LogicalName, out var detail);
        detail ??= new EntityDetail { LogicalName = c.Entity.LogicalName };

        var priority = c.Score >= 70 ? "High" : c.Score >= 40 ? "Medium" : "Low";
        if (i >= 8) phase = 2;
        if (i >= 15) phase = 3;

        var useful = detail.Attributes
            .Where(a => a.UsefulForReporting && IsPrimaryReportingAttribute(a.LogicalName, a.AttributeType))
            .OrderBy(a => ReportingFieldRank(a.AttributeType))
            .ThenBy(a => a.LogicalName)
            .Take(18)
            .ToList();
        var money = detail.Attributes.Where(a => string.Equals(a.AttributeType, "Money", StringComparison.OrdinalIgnoreCase)).Select(a => a.LogicalName);
        var dates = detail.Attributes.Where(a => string.Equals(a.AttributeType, "DateTime", StringComparison.OrdinalIgnoreCase)).Select(a => a.LogicalName);
        var statusish = detail.Attributes.Where(a =>
                a.AttributeType is not null &&
                (a.AttributeType.Equals("State", StringComparison.OrdinalIgnoreCase)
                 || a.AttributeType.Equals("Status", StringComparison.OrdinalIgnoreCase)
                 || a.AttributeType.Equals("Picklist", StringComparison.OrdinalIgnoreCase)
                 || a.AttributeType.Equals("Owner", StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.LogicalName);

        var approvedRels = detail.Relationships.Where(r => r.TouchesApprovedEntity).Take(12).ToList();

        sb.AppendLine($"### {i + 1}. `{c.Entity.LogicalName}` — {c.Entity.DisplayName}");
        sb.AppendLine();
        sb.AppendLine($"- **Standard/Custom:** {(c.Entity.IsCustomEntity ? "Custom" : "Standard")}");
        sb.AppendLine($"- **Reporting priority:** {priority} (score={c.Score:0})");
        sb.AppendLine($"- **Recommended approval phase:** Phase {phase}");
        sb.AppendLine($"- **Currently approved:** {(c.Entity.IsCurrentlyApproved ? "Yes" : "No")}");
        sb.AppendLine($"- **Observed report evidence:** {c.ReportDistinctCount} reportentities link row(s) (entity logical name references)");
        sb.AppendLine($"- **Observed view evidence:** {c.ViewCount} savedquery/userquery row(s) with matching ReturnedTypeCode");
        sb.AppendLine($"- **Ownership:** {c.Entity.OwnershipType}; Advanced Find valid: {c.Entity.IsValidForAdvancedFind?.ToString() ?? "unknown"}");
        sb.AppendLine($"- **Counts (observed):** attributes={c.Entity.AttributesCount?.ToString() ?? "?"}, relationships={c.Entity.RelationshipsCount?.ToString() ?? "?"}");
        sb.AppendLine();
        sb.AppendLine("**Business use cases (recommendations):**");
        foreach (var q in ExampleQuestions(c.Entity.LogicalName))
        {
            sb.AppendLine($"- {q.Question} — feasibility: {q.Feasibility}");
        }

        sb.AppendLine();
        sb.AppendLine("**Relevant fields (observed sample of reporting-useful attributes):**");
        if (useful.Count == 0)
        {
            sb.AppendLine("- (detail unavailable or no read attributes returned)");
        }
        else
        {
            foreach (var a in useful)
            {
                var extra = a.LookupTargets is { Length: > 0 }
                    ? $" targets=[{string.Join(',', a.LookupTargets)}]"
                    : a.OptionSetSummary is { Count: > 0 }
                        ? $" options=[{string.Join("; ", a.OptionSetSummary.Take(6))}]"
                        : "";
                sb.AppendLine($"- `{a.LogicalName}` ({a.AttributeType}) — {a.DisplayName}{extra}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"- Money fields: {JoinOrNone(money)}");
        sb.AppendLine($"- DateTime fields: {JoinOrNone(dates.Take(12))}");
        sb.AppendLine($"- Status/state/picklist/owner fields: {JoinOrNone(statusish.Take(12))}");
        sb.AppendLine();
        sb.AppendLine("**Relationships touching approved entities (observed, capped):**");
        if (approvedRels.Count == 0)
        {
            sb.AppendLine("- None detected in capped discovery set (may still exist outside filter/cap).");
        }
        else
        {
            foreach (var r in approvedRels)
            {
                sb.AppendLine(
                    $"- `{r.SchemaName}` [{r.Kind}] {r.ReferencingEntity}.{r.ReferencingAttribute} → {r.ReferencedEntity}.{r.ReferencedAttribute}" +
                    (r.MayDuplicateRootViaOneToMany ? " *(1:N may duplicate root rows)*" : ""));
            }
        }

        sb.AppendLine();
        sb.AppendLine("**Query engine limitations for this entity:**");
        sb.AppendLine("- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.");
        sb.AppendLine("- Single-hop / two-hop links: supported for list/count only.");
        sb.AppendLine("- **Aggregate + linksJson: unsupported** (current engine).");
        if (string.Equals(c.Entity.LogicalName, "salesorderdetail", StringComparison.OrdinalIgnoreCase) ||
            c.Entity.LogicalName.EndsWith("detail", StringComparison.OrdinalIgnoreCase) ||
            c.Entity.LogicalName.Contains("product", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("- Line-item aggregates joined to parent headers require either root=line-item entity or future aggregate+link support.");
        }

        foreach (var n in detail.Notes)
        {
            sb.AppendLine($"- Note: {n}");
        }

        sb.AppendLine();
    }

    sb.AppendLine("## Additional high-scoring entities (not in detailed top 20)");
    sb.AppendLine();
    foreach (var c in ranked.Skip(20).Take(15))
    {
        sb.AppendLine(
            $"- `{c.Entity.LogicalName}` ({c.Entity.DisplayName}) score={c.Score:0}, reports={c.ReportDistinctCount}, views={c.ViewCount}, custom={c.Entity.IsCustomEntity}");
    }

    sb.AppendLine();
    sb.AppendLine("## Suggested approval rollout");
    sb.AppendLine();
    sb.AppendLine("1. **Phase 1 (high value, low ambiguity):** opportunity, quote, invoice, salesorderdetail, product, systemuser, businessunit");
    sb.AppendLine("2. **Phase 2 (pipeline depth):** quotedetail, invoicedetail, opportunityproduct, campaign (+ campaignresponse/activity if used)");
    sb.AppendLine("3. **Phase 3 (custom WE entities):** evaluate top `te_` / `ted_` entities that have report/view evidence and Money/DateTime fields — do not approve by name alone.");
    sb.AppendLine();
    sb.AppendLine("## Files");
    sb.AppendLine();
    sb.AppendLine("- `crm-entity-inventory.json` — full compact entity inventory");
    sb.AppendLine("- `crm-report-inventory.json` — reports/views inventory + safe entity references");
    sb.AppendLine("- `crm-report-entity-recommendations.md` — this ranked shortlist");
    sb.AppendLine();
    return sb.ToString();
}

static IEnumerable<(string Question, string Feasibility)> ExampleQuestions(string logical)
{
    switch (logical.ToLowerInvariant())
    {
        case "opportunity":
            yield return ("How many open opportunities by month this year?", "Supported: aggregate + date + groupBy month (root-only)");
            yield return ("Top opportunities with account name", "Supported: list + linksJson to account");
            yield return ("Opportunity revenue by account this quarter", "Unsupported until aggregate+linksJson");
            yield break;
        case "salesorderdetail":
            yield return ("Monthly ordered quantity by product for 2025", "Supported if aggregating on salesorderdetail root");
            yield return ("Order lines for a sales order with product name", "Supported: list + linksJson product");
            yield return ("Total line amount by customer account", "Unsupported as aggregate+link; needs engine enhancement");
            yield break;
        case "quote":
            yield return ("Count of quotes created last 8 calendar months", "Supported: count/aggregate + dateRange");
            yield return ("Quotes with customer account", "Supported: list + linksJson");
            yield break;
        case "invoice":
            yield return ("Invoiced amount by month for 2025", "Supported: aggregate on invoice root if Money + date fields exist");
            yield return ("Invoices with related sales order", "Supported: list + linksJson (not aggregate+link)");
            yield break;
        case "product":
            yield return ("List active products", "Supported: list + status filter");
            yield return ("Count products by product type", "Supported: aggregate + groupBy picklist if present");
            yield break;
        case "campaign":
            yield return ("Campaigns started this year", "Supported: list/count + date filter");
            yield return ("Campaign responses by campaign", "May require campaignresponse approval; aggregate+link unsupported");
            yield break;
        case "systemuser":
            yield return ("List users", "Supported: list on systemuser");
            yield return ("Count orders by owner", "Supported via aggregate groupBy ownerid on salesorder; systemuser helps list enrichment");
            yield break;
        case "businessunit":
            yield return ("List business units", "Supported: list");
            yield return ("Orders by business unit", "Supported: aggregate groupBy owningbusinessunit on salesorder");
            yield break;
        default:
            yield return ("List recent records", "Supported when approved: list + sort + date");
            yield return ("Count records in a calendar period", "Supported: count/aggregate + dateRange");
            yield return ("Aggregate with related entity attributes", "Unsupported: aggregate + linksJson");
            yield break;
    }
}
static string JoinOrNone(IEnumerable<string?> values)
{
    var arr = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => $"`{v}`").ToArray();
    return arr.Length == 0 ? "(none in detail sample)" : string.Join(", ", arr);
}

static string EscapeOData(string value) => value.Replace("'", "''", StringComparison.Ordinal);

static bool TryGetPropertyIgnoreCase(JsonElement el, string name, out JsonElement value)
{
    if (el.TryGetProperty(name, out value))
    {
        return true;
    }

    foreach (var prop in el.EnumerateObject())
    {
        if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            value = prop.Value;
            return true;
        }
    }

    value = default;
    return false;
}

static string? GetString(JsonElement el, string name)
{
    if (!TryGetPropertyIgnoreCase(el, name, out var p))
    {
        return null;
    }

    return p.ValueKind switch
    {
        JsonValueKind.String => p.GetString(),
        JsonValueKind.Number => p.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => null,
        JsonValueKind.Object when p.TryGetProperty("Value", out var v) =>
            v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString(),
        _ => p.ToString()
    };
}

static int? GetInt(JsonElement el, string name)
{
    if (!TryGetPropertyIgnoreCase(el, name, out var p))
    {
        return null;
    }

    if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i))
    {
        return i;
    }

    if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var s))
    {
        return s;
    }

    // Metadata enums often serialize as { "Value": 1 }
    if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("Value", out var v))
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var ev))
        {
            return ev;
        }

        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var es))
        {
            return es;
        }
    }

    return null;
}

static bool? GetBool(JsonElement el, string name)
{
    if (!TryGetPropertyIgnoreCase(el, name, out var p))
    {
        return null;
    }

    return p.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}

static Guid? GetGuid(JsonElement el, string name)
{
    var s = GetString(el, name);
    return Guid.TryParse(s, out var g) ? g : null;
}

static string? GetLocalized(JsonElement el, string name)
{
    if (!el.TryGetProperty(name, out var lab) || lab.ValueKind != JsonValueKind.Object)
    {
        return null;
    }

    if (lab.TryGetProperty("UserLocalizedLabel", out var ull) &&
        ull.ValueKind == JsonValueKind.Object &&
        ull.TryGetProperty("Label", out var label))
    {
        return label.GetString();
    }

    if (lab.TryGetProperty("LocalizedLabels", out var list) && list.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetProperty("LanguageCode", out var lc) && lc.TryGetInt32(out var code) && code == 1033 &&
                item.TryGetProperty("Label", out var en))
            {
                return en.GetString();
            }
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetProperty("Label", out var any))
            {
                return any.GetString();
            }
        }
    }

    return null;
}

static string MapOwnership(int? value, string? raw) => value switch
{
    0 => "None",
    1 => "UserOwned",
    2 => "BusinessOwned",
    3 => "OrganizationOwned",
    _ when !string.IsNullOrWhiteSpace(raw) && !raw.Contains('{', StringComparison.Ordinal) => raw!,
    _ => value?.ToString() ?? "Unknown"
};

static int ReportingFieldRank(string? type) => (type ?? "").ToLowerInvariant() switch
{
    "money" => 0,
    "decimal" or "double" or "integer" or "bigint" => 1,
    "datetime" => 2,
    "state" or "status" or "picklist" or "owner" or "customer" or "lookup" => 3,
    "boolean" => 4,
    "string" or "memo" => 8,
    _ => 9
};

static bool IsPrimaryReportingAttribute(string? logical, string? type)
{
    if (string.IsNullOrWhiteSpace(logical))
    {
        return false;
    }

    var n = logical.ToLowerInvariant();
    if (n.StartsWith("address", StringComparison.Ordinal)
        || n.StartsWith("yomi", StringComparison.Ordinal)
        || n.StartsWith("entityimage", StringComparison.Ordinal)
        || n is "traversedpath" or "passporthi" or "passportlo" or "utcconversiontimezonecode"
        || n.EndsWith("name", StringComparison.Ordinal) && (type?.Equals("String", StringComparison.OrdinalIgnoreCase) ?? false))
    {
        return false;
    }

    return true;
}

static string Truncate(string? s, int max) =>
    string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

sealed class EntityInventoryRow
{
    public required string LogicalName { get; init; }
    public string? SchemaName { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? EntitySetName { get; init; }
    public string? PrimaryIdAttribute { get; init; }
    public string? PrimaryNameAttribute { get; init; }
    public bool IsCustomEntity { get; init; }
    public string? OwnershipType { get; init; }
    public bool? IsValidForAdvancedFind { get; init; }
    public int? ObjectTypeCode { get; init; }
    public bool IsCurrentlyApproved { get; init; }
    public bool IsTeOrTedCustom { get; init; }
    public int? AttributesCount { get; set; }
    public int? RelationshipsCount { get; set; }
}

sealed class ReportRow
{
    public Guid? ReportId { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? FileName { get; init; }
    public int? ReportTypeCode { get; init; }
    public bool? IsCustomReport { get; init; }
    public bool? IsPersonal { get; init; }
    public int? LanguageCode { get; init; }
    public string? CreatedOn { get; init; }
    public string? ModifiedOn { get; init; }
}

sealed class ReportEntityRef
{
    public Guid? ReportId { get; init; }
    public int? ObjectTypeCode { get; init; }
    public string? EntityLogicalName { get; init; }
    public string? EvidenceKind { get; init; }
}

sealed class ViewRow
{
    public Guid? ViewId { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? ReturnedTypeCode { get; init; }
    public int? QueryType { get; init; }
    public bool? IsQuickFind { get; init; }
    public bool? IsDefault { get; init; }
    public bool? IsPrivate { get; init; }
    public string? CreatedOn { get; init; }
    public string? ModifiedOn { get; init; }
    public string? Source { get; init; }
}

sealed class RankedCandidate
{
    public required EntityInventoryRow Entity { get; init; }
    public double Score { get; init; }
    public int ReportDistinctCount { get; init; }
    public int ViewCount { get; init; }
    public bool NamedCandidate { get; init; }
    public bool SalesMarketingHint { get; init; }
}

sealed class EntityDetail
{
    public required string LogicalName { get; init; }
    public List<AttributeDetail> Attributes { get; set; } = [];
    public List<RelationshipDetail> Relationships { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

sealed class AttributeDetail
{
    public string? LogicalName { get; set; }
    public string? DisplayName { get; set; }
    public string? AttributeType { get; set; }
    public bool? IsValidForRead { get; set; }
    public bool? IsValidForGrid { get; set; }
    public bool? IsCustomAttribute { get; set; }
    public bool UsefulForReporting { get; set; }
    public string[]? LookupTargets { get; set; }
    public List<string>? OptionSetSummary { get; set; }
}

sealed class RelationshipDetail
{
    public string? SchemaName { get; set; }
    public string? Kind { get; set; }
    public string? ReferencingEntity { get; set; }
    public string? ReferencedEntity { get; set; }
    public string? ReferencingAttribute { get; set; }
    public string? ReferencedAttribute { get; set; }
    public bool TouchesApprovedEntity { get; set; }
    public bool MayDuplicateRootViaOneToMany { get; set; }
}
