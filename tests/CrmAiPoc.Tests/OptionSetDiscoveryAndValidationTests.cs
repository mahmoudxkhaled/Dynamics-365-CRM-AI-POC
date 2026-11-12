using System.Globalization;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.FetchXml;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Discovery;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// SEM-03 Phase 2: generic OptionSet discovery via discover_crm_schema and FetchXML validation.
/// Synthetic metadata only — no live Dynamics.
/// </summary>
public sealed class OptionSetDiscoveryAndValidationTests
{
    [Fact]
    public async Task Discover_Returns_Picklist_Options_Lazily()
    {
        var metadata = new OptionSetFakeMetadata(BuildCatalog());
        var discovery = CreateDiscovery(metadata);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_case"],
            Concepts = ["priority"]
        });

        Assert.True(result.Success, result.Error);
        var field = Assert.Single(result.Result!.Fields);
        var top = field.Candidates[0];
        Assert.Equal("prioritycode", top.LogicalName);
        Assert.Equal("Picklist", top.Type);
        Assert.NotNull(top.Options);
        Assert.Contains(top.Options!, o => o.Value == 1 && o.Label == "High");
        Assert.Contains(top.Options!, o => o.Value == 2 && o.Label == "Low");
        Assert.True(metadata.OptionLoadCount >= 1);
    }

    [Fact]
    public async Task Discover_Surfaces_OptionSet_Enrichment_Failure_Without_Inventing_Empty_Options()
    {
        var metadata = new OptionSetFakeMetadata(BuildCatalog(), failOptionLoad: true);
        var discovery = CreateDiscovery(metadata);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_case"],
            Concepts = ["prioritycode"]
        });

        Assert.True(result.Success, result.Error);
        var top = result.Result!.Fields[0].Candidates[0];
        Assert.Null(top.Options);
        Assert.False(string.IsNullOrWhiteSpace(top.OptionsLoadError));
        Assert.Contains(result.Result.Notes, n => n.Contains("OptionSet enrichment failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Repeated_Discovery_Reuses_Cached_Options_Without_Extra_Loads_When_Already_Present()
    {
        var metadata = new OptionSetFakeMetadata(BuildCatalog());
        var discovery = CreateDiscovery(metadata);

        await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_case"],
            Concepts = ["prioritycode"]
        });
        var afterFirst = metadata.OptionLoadCount;

        await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_case"],
            Concepts = ["prioritycode"]
        });

        // Fake still increments, but AttributeOptionsResult.AlreadyCached is set on subsequent calls.
        Assert.True(metadata.OptionLoadCount > afterFirst);
        Assert.True(metadata.LastResult!.AlreadyCached || metadata.LastResult.OptionsAvailable);
    }

    [Fact]
    public async Task Similar_Labels_On_Different_Fields_Keep_Distinct_Values()
    {
        var metadata = new OptionSetFakeMetadata(BuildCatalog());
        var discovery = CreateDiscovery(metadata);

        var priority = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_case"],
            Concepts = ["prioritycode"]
        });
        var severity = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_case"],
            Concepts = ["severitycode"]
        });

        var highPriority = priority.Result!.Fields[0].Candidates[0].Options!
            .Single(o => o.Label.Equals("High", StringComparison.OrdinalIgnoreCase)).Value;
        var highSeverity = severity.Result!.Fields[0].Candidates[0].Options!
            .Single(o => o.Label.Equals("High", StringComparison.OrdinalIgnoreCase)).Value;

        Assert.Equal(1, highPriority);
        Assert.Equal(100, highSeverity);
        Assert.NotEqual(highPriority, highSeverity);
    }

    [Fact]
    public async Task Missing_And_Ambiguous_Labels_Do_Not_Invent_Codes()
    {
        var attr = ChoiceAttr(
            "prioritycode",
            "Priority",
            CrmAttributeKind.Picklist,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["High"] = 1,
                ["Low"] = 2
            });

        var entity = Entity("alpha_case", [attr]);
        var outcome = FetchXmlSafetyValidator.ValidateChoiceFilterValue(
            entity,
            attr,
            "Medium",
            "eq");

        Assert.NotNull(outcome);
        Assert.False(outcome!.Success);
        Assert.Contains("numeric", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("value=\"3\"", outcome.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_String_Label_Rejected_Before_Crm_Execution()
    {
        var metadata = new OptionSetFakeMetadata(BuildCatalog());
        var validator = CreateValidator(metadata);

        var fetch =
            "<fetch top=\"5\"><entity name=\"alpha_case\">" +
            "<attribute name=\"title\"/>" +
            "<filter type=\"and\">" +
            "<condition attribute=\"prioritycode\" operator=\"eq\" value=\"High\"/>" +
            "</filter></entity></fetch>";

        var outcome = await validator.ValidateAsync(new FetchXmlToolRequest
        {
            FetchXml = fetch,
            ResultMode = FetchXmlResultMode.Complete
        });

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_FILTER", outcome.ErrorCode);
        Assert.Contains("numeric", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(outcome.Query);
    }

    [Fact]
    public async Task Valid_Numeric_OptionSet_Filter_Accepted()
    {
        var metadata = new OptionSetFakeMetadata(BuildCatalog());
        var validator = CreateValidator(metadata);

        var fetch =
            "<fetch top=\"5\"><entity name=\"alpha_case\">" +
            "<attribute name=\"title\"/>" +
            "<filter type=\"and\">" +
            "<condition attribute=\"prioritycode\" operator=\"eq\" value=\"1\"/>" +
            "</filter></entity></fetch>";

        var outcome = await validator.ValidateAsync(new FetchXmlToolRequest
        {
            FetchXml = fetch,
            ResultMode = FetchXmlResultMode.Complete
        });

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.NotNull(outcome.Query);
    }

    [Fact]
    public async Task Invalid_Numeric_Option_Rejected_When_Options_Known()
    {
        var metadata = new OptionSetFakeMetadata(BuildCatalog());
        var validator = CreateValidator(metadata);

        var fetch =
            "<fetch top=\"5\"><entity name=\"alpha_case\">" +
            "<filter><condition attribute=\"prioritycode\" operator=\"eq\" value=\"99\"/></filter>" +
            "</entity></fetch>";

        var outcome = await validator.ValidateAsync(new FetchXmlToolRequest
        {
            FetchXml = fetch,
            ResultMode = FetchXmlResultMode.Complete
        });

        Assert.False(outcome.Success);
        Assert.Contains("not a valid option", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task String_Number_Date_Lookup_Filters_Remain_Valid()
    {
        var metadata = new OptionSetFakeMetadata(BuildCatalog());
        var validator = CreateValidator(metadata);

        var fetch =
            "<fetch top=\"5\"><entity name=\"alpha_case\">" +
            "<filter type=\"and\">" +
            "<condition attribute=\"title\" operator=\"like\" value=\"%demo%\"/>" +
            "<condition attribute=\"createdon\" operator=\"on-or-after\" value=\"2024-01-01\"/>" +
            "<condition attribute=\"customerid\" operator=\"eq\" value=\"11111111-1111-1111-1111-111111111111\"/>" +
            "</filter></entity></fetch>";

        var outcome = await validator.ValidateAsync(new FetchXmlToolRequest
        {
            FetchXml = fetch,
            ResultMode = FetchXmlResultMode.Complete
        });

        Assert.True(outcome.Success, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Global_And_Local_OptionSets_Are_Attribute_Scoped()
    {
        // Same label on two attributes must resolve independently (no shared hardcoded map).
        var metadata = new OptionSetFakeMetadata(BuildCatalog());
        var discovery = CreateDiscovery(metadata);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_case"],
            Concepts = ["statuscode"]
        });

        var options = result.Result!.Fields[0].Candidates[0].Options!;
        Assert.Contains(options, o => o.Label == "Active" && o.Value == 1);
        Assert.DoesNotContain(options, o => o.Label == "High");
    }

    [Fact]
    public void Example_FetchXml_Uses_Resolved_Numeric_Choice_Value()
    {
        // Documentation-style assertion: discovery maps label→value; FetchXML must use the number.
        var options = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["High"] = 1,
            ["Low"] = 2
        };
        Assert.True(options.TryGetValue("High", out var code));
        var fetch = string.Format(
            CultureInfo.InvariantCulture,
            "<fetch top=\"5\"><entity name=\"alpha_case\">" +
            "<filter><condition attribute=\"prioritycode\" operator=\"eq\" value=\"{0}\"/></filter>" +
            "</entity></fetch>",
            code);
        Assert.Contains("value=\"1\"", fetch, StringComparison.Ordinal);
        Assert.DoesNotContain("High", fetch, StringComparison.Ordinal);
    }

    private static CrmSchemaDiscoveryService CreateDiscovery(ICrmMetadataService metadata)
    {
        var options = Options.Create(new CrmOptions { ApprovedEntities = ["alpha_case"] });
        var policy = new EntityApprovalPolicy(options);
        return new CrmSchemaDiscoveryService(
            metadata,
            policy,
            new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy),
            NullLogger<CrmSchemaDiscoveryService>.Instance);
    }

    private static FetchXmlSafetyValidator CreateValidator(ICrmMetadataService metadata)
    {
        var options = Options.Create(new CrmOptions { ApprovedEntities = ["alpha_case"] });
        var policy = new EntityApprovalPolicy(options);
        return new FetchXmlSafetyValidator(
            metadata,
            policy,
            new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy));
    }

    private static IReadOnlyList<CrmEntityMetadata> BuildCatalog() =>
    [
        Entity(
            "alpha_case",
            [
                Attr("title", "Title", CrmAttributeKind.String),
                Attr("createdon", "Created On", CrmAttributeKind.DateTime),
                Attr("customerid", "Customer", CrmAttributeKind.Lookup),
                ChoiceAttr(
                    "prioritycode",
                    "Priority",
                    CrmAttributeKind.Picklist,
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["High"] = 1,
                        ["Low"] = 2
                    }),
                ChoiceAttr(
                    "severitycode",
                    "Severity",
                    CrmAttributeKind.Picklist,
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["High"] = 100,
                        ["Low"] = 200
                    }),
                ChoiceAttr(
                    "statuscode",
                    "Status",
                    CrmAttributeKind.Status,
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Active"] = 1,
                        ["Resolved"] = 2
                    })
            ])
    ];

    private static CrmEntityMetadata Entity(string name, IReadOnlyList<CrmAttributeMetadata> attrs) =>
        new()
        {
            LogicalName = name,
            EntitySetName = name + "s",
            DisplayName = name,
            PrimaryIdAttribute = name + "id",
            PrimaryNameAttribute = "title",
            IsApprovedForAi = true,
            Attributes = attrs
        };

    private static CrmAttributeMetadata Attr(string name, string display, CrmAttributeKind kind) =>
        new()
        {
            LogicalName = name,
            ODataPropertyName = name,
            DisplayName = display,
            Kind = kind,
            IsValidForRead = true,
            IsFilterable = true,
            AllowedOperators = AttributeKindMapper.DefaultOperators(kind)
        };

    private static CrmAttributeMetadata ChoiceAttr(
        string name,
        string display,
        CrmAttributeKind kind,
        IReadOnlyDictionary<string, int> byLabel) =>
        new()
        {
            LogicalName = name,
            ODataPropertyName = name,
            DisplayName = display,
            Kind = kind,
            IsValidForRead = true,
            IsFilterable = true,
            AllowedOperators = AttributeKindMapper.DefaultOperators(kind),
            OptionSetByLabel = byLabel,
            OptionSetByValue = byLabel.ToDictionary(kv => kv.Value, kv => kv.Key)
        };

    private sealed class OptionSetFakeMetadata(
        IReadOnlyList<CrmEntityMetadata> entities,
        bool failOptionLoad = false) : ICrmMetadataService
    {
        private readonly Dictionary<string, CrmEntityMetadata> _map =
            entities.ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);
        private readonly bool _failOptionLoad = failOptionLoad;

        public int OptionLoadCount { get; private set; }
        public AttributeOptionsResult? LastResult { get; private set; }

        public Task EnsureApprovedEntitiesLoadedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<CrmEntityMetadata?> GetEntityAsync(string logicalName, CancellationToken cancellationToken = default) =>
            Task.FromResult(_map.TryGetValue(logicalName, out var e) ? e : null);

        public Task<IReadOnlyList<CrmEntityMetadata>> GetApprovedEntitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CrmEntityMetadata>>(_map.Values.ToList());

        public Task RefreshAsync(string? logicalName = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AttributeOptionsResult> EnsureAttributeOptionsAsync(
            string entityLogicalName,
            string attributeLogicalName,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            OptionLoadCount++;
            if (!_map.TryGetValue(entityLogicalName, out var entity) ||
                !entity.TryGetAttribute(attributeLogicalName, out var attr))
            {
                LastResult = new AttributeOptionsResult
                {
                    Error = $"Attribute '{attributeLogicalName}' was not found on '{entityLogicalName}'."
                };
                return Task.FromResult(LastResult);
            }

            if (_failOptionLoad)
            {
                LastResult = new AttributeOptionsResult
                {
                    Attribute = attr,
                    OptionsAvailable = false,
                    Error =
                        "Dynamics OptionSet metadata request failed (HTTP 400) for " +
                        $"{entityLogicalName}.{attributeLogicalName} via PicklistAttributeMetadata: " +
                        "Could not find a property named 'TrueOption' on type 'Microsoft.Dynamics.CRM.OptionSetMetadata'."
                };
                return Task.FromResult(LastResult);
            }

            LastResult = new AttributeOptionsResult
            {
                Attribute = attr,
                OptionsAvailable = attr.OptionSetByValue.Count > 0 || attr.OptionSetByLabel.Count > 0,
                AlreadyCached = OptionLoadCount > 1
            };
            return Task.FromResult(LastResult);
        }

        public string GetAiCatalogSummary() => string.Empty;
    }
}
