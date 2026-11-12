using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Discovery;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Mocked POC tests for on-demand lexical CRM schema discovery (no live CRM / no embeddings).
/// </summary>
public sealed class CrmSchemaDiscoveryTests
{
    [Fact]
    public async Task Ranks_Target_Attribute_Near_End_Of_Large_Collection()
    {
        var metadata = LargeSalesOrderFixture(attributeCount: 320, targetIndex: 310);
        var service = CreateService(metadata);

        var result = await service.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["salesorder"],
            Concepts = ["order total value"]
        });

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Result);
        var match = Assert.Single(result.Result!.Fields);
        Assert.Equal("order total value", match.Concept);
        Assert.Contains(match.Candidates, c => c.LogicalName == "totalamount");
        Assert.Equal("totalamount", match.Candidates[0].LogicalName);
        Assert.True(result.Result.ResponseCharacters > 0);
        Assert.DoesNotContain("ORD-", result.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ranking_Is_Independent_Of_Metadata_Order()
    {
        var forward = LargeSalesOrderFixture(attributeCount: 200, targetIndex: 5, shuffleSeed: null);
        var reverse = LargeSalesOrderFixture(attributeCount: 200, targetIndex: 5, reverse: true);

        var a = await CreateService(forward).DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["salesorder"],
            Concepts = ["total amount"]
        });
        var b = await CreateService(reverse).DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["salesorder"],
            Concepts = ["total amount"]
        });

        Assert.Equal(a.Result!.Fields[0].Candidates[0].LogicalName, b.Result!.Fields[0].Candidates[0].LogicalName);
        Assert.Equal("totalamount", a.Result.Fields[0].Candidates[0].LogicalName);
    }

    [Fact]
    public async Task Returns_Multiple_Relationship_Candidates_Without_Auto_Picking()
    {
        var metadata = RelationshipFixture();
        var service = CreateService(metadata);

        var result = await service.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["salesorder", "account"],
            RelationshipPairs = [("salesorder", "account")]
        });

        Assert.True(result.Success, result.Error);
        var group = Assert.Single(result.Result!.Relationships);
        Assert.True(group.Candidates.Count >= 2);
        Assert.Contains(group.Candidates, c => c.SchemaName == "te_account_salesorder_customeraccountid");
        Assert.Contains(group.Candidates, c => c.SchemaName == "te_account_salesorder_branchaccountid");
    }

    [Fact]
    public async Task Unresolved_Concept_Is_Reported_Explicitly()
    {
        var metadata = LargeSalesOrderFixture(attributeCount: 50, targetIndex: 10);
        var service = CreateService(metadata);

        var result = await service.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["salesorder"],
            Concepts = ["intergalactic warp signature"]
        });

        Assert.True(result.Success, result.Error);
        Assert.Contains("intergalactic warp signature", result.Result!.UnresolvedConcepts);
        Assert.Empty(result.Result.Fields);
    }

    [Fact]
    public async Task Keeps_Ambiguous_Alternatives_Not_Just_First_Hit()
    {
        var metadata = new FakeMetadata([
            new CrmEntityMetadata
            {
                LogicalName = "salesorder",
                EntitySetName = "salesorders",
                DisplayName = "Sales Order",
                PrimaryIdAttribute = "salesorderid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true,
                Attributes =
                [
                    Attr("totalamount", "Total Amount", CrmAttributeKind.Money),
                    Attr("totallineitemamount", "Total Line Item Amount", CrmAttributeKind.Money),
                    Attr("totalamountlessfreight", "Total Amount Less Freight", CrmAttributeKind.Money),
                    Attr("name", "Name", CrmAttributeKind.String)
                ]
            }
        ]);

        var result = await CreateService(metadata).DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["salesorder"],
            Concepts = ["total amount"]
        });

        Assert.True(result.Result!.Fields[0].Candidates.Count >= 2);
        Assert.Contains(result.Result.Fields[0].Candidates, c => c.LogicalName == "totalamount");
        Assert.Contains(result.Result.Fields[0].Candidates, c => c.LogicalName.Contains("total", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Chat_Discover_Then_Query_Uses_Request_Local_Tool_Messages_Only()
    {
        var discoverArgs = new Dictionary<string, object?>
        {
            ["entities"] = new[] { "salesorder" },
            ["concepts"] = new[] { "order total value" }
        };
        var chat = new ScriptedChatClient(
            new ChatResponse([
                new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("d1", "discover_crm_schema", discoverArgs)
                ])
            ]),
            new ChatResponse([
                new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("q1", "query_crm", new Dictionary<string, object?>
                    {
                        ["fetchXml"] =
                            """
                            <fetch top="5">
                              <entity name="salesorder">
                                <attribute name="name" />
                                <attribute name="totalamount" />
                                <order attribute="totalamount" descending="true" />
                              </entity>
                            </fetch>
                            """,
                        ["resultMode"] = "complete"
                    })
                ])
            ]));

        var crmOptions = Options.Create(new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = false,
            ApprovedEntities = ["salesorder", "account", "contact"]
        });
        var aiOptions = Options.Create(new AiOptions
        {
            MaxQueryPlanCorrectionRetries = 1,
            MaxSchemaDiscoveryCallsPerRequest = 2
        });
        var approval = new EntityApprovalPolicy(crmOptions);
        var metadata = LargeSalesOrderFixture(80, 40);
        var discovery = new CrmSchemaDiscoveryService(
            metadata,
            approval,
            new CrmAuthorizationService(crmOptions, NullLogger<CrmAuthorizationService>.Instance, approval),
            NullLogger<CrmSchemaDiscoveryService>.Instance);
        var executor = new RecordingExecutor();
        var store = new ConversationStore();
        var http = new CrmAiPoc.Api.Crm.Dynamics.DynamicsCrmHttp(
            new StubHttp(),
            crmOptions,
            NullLogger<CrmAiPoc.Api.Crm.Dynamics.DynamicsCrmHttp>.Instance);
        var service = new ChatService(
            chat,
            new CrmAiTools(NullLogger<CrmAiTools>.Instance),
            store,
            new CrmAiPoc.Api.Crm.Dynamics.DynamicsConnectivityService(
                http, crmOptions, NullLogger<CrmAiPoc.Api.Crm.Dynamics.DynamicsConnectivityService>.Instance),
            executor,
            metadata,
            discovery,
            approval,
            crmOptions,
            aiOptions,
            NullLogger<ChatService>.Instance);

        var response = await service.ChatAsync(
            new CrmAiPoc.Api.Models.ChatRequestDto
            {
                ConversationId = "disc-1",
                Message = "Show me our biggest orders this year."
            },
            CancellationToken.None);

        Assert.Equal(2, chat.CallCount);
        Assert.Equal(1, executor.QueryCount);
        Assert.True(string.IsNullOrWhiteSpace(response.Reply));
        Assert.NotNull(response.StructuredData);
        Assert.Equal(1, store.GetOrCreate("disc-1").UserQuestionCount);
        Assert.Contains(chat.CapturedToolResults, fr => OutboundAiPayloadAuditor.IsSchemaDiscoveryResult(fr.Result));
        Assert.DoesNotContain(
            store.GetOrCreate("disc-1").GetUserQuestionsSnapshot(),
            q => q.Contains("totalamount", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ArgumentParser_Accepts_RelationshipPairsJson_Alias()
    {
        var parsed = CrmSchemaDiscoveryArgumentParser.Parse(new Dictionary<string, object?>
        {
            ["entities"] = new[] { "salesorder", "account" },
            ["concepts"] = new[] { "order total value" },
            ["relationshipPairsJson"] = """[["salesorder","account"],["salesorder","contact"]]"""
        });

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(2, parsed.Request!.RelationshipPairs.Count);
        Assert.Contains(parsed.Request.RelationshipPairs, p =>
            p.Left.Equals("salesorder", StringComparison.OrdinalIgnoreCase) &&
            p.Right.Equals("account", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Auditor_Allows_Schema_Discovery_Tool_Results()
    {
        var payload = """{"fields":[{"concept":"x","entity":"salesorder","candidates":[{"logicalName":"totalamount","displayName":"Total Amount","type":"Money","score":90}]}],"relationships":[],"unresolvedConcepts":[]}""";
        var ok = OutboundAiPayloadAuditor.Audit([
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "q"),
            new(ChatRole.Assistant, [new FunctionCallContent("1", "discover_crm_schema")]),
            new(ChatRole.Tool, [new FunctionResultContent("1", payload)])
        ]);
        Assert.True(ok.SafeForLiveCrm);
        Assert.True(OutboundAiPayloadAuditor.IsSchemaDiscoveryResult(payload));
    }

    [Fact]
    public async Task Discovery_Skips_LegacyStaticEntity_When_Removed_From_ApprovedEntities()
    {
        // salesorder is still on CrmEntityAllowlist but omitted from ApprovedEntities.
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact"]
        });
        var policy = new EntityApprovalPolicy(options);
        var service = new CrmSchemaDiscoveryService(
            LargeSalesOrderFixture(20, 5),
            policy,
            new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy),
            NullLogger<CrmSchemaDiscoveryService>.Instance);

        var result = await service.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["salesorder"],
            Concepts = ["total amount"]
        });

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Result);
        Assert.Empty(result.Result!.Fields);
        Assert.Contains(result.Result.UnresolvedConcepts, c =>
            c.Equals("total amount", StringComparison.OrdinalIgnoreCase));
    }

    private static CrmSchemaDiscoveryService CreateService(ICrmMetadataService metadata)
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["salesorder", "account", "contact", "opportunity"]
        });
        var policy = new EntityApprovalPolicy(options);
        return new CrmSchemaDiscoveryService(
            metadata,
            policy,
            new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy),
            NullLogger<CrmSchemaDiscoveryService>.Instance);
    }

    private static FakeMetadata LargeSalesOrderFixture(
        int attributeCount,
        int targetIndex,
        int? shuffleSeed = 7,
        bool reverse = false)
    {
        var attrs = new List<CrmAttributeMetadata>(attributeCount);
        for (var i = 0; i < attributeCount; i++)
        {
            if (i == targetIndex)
            {
                attrs.Add(Attr("totalamount", "Total Amount", CrmAttributeKind.Money, sortable: true));
                continue;
            }

            attrs.Add(Attr($"attr{i:000}", $"Attribute {i:000}", CrmAttributeKind.String));
        }

        if (reverse)
        {
            attrs.Reverse();
        }
        else if (shuffleSeed is int seed)
        {
            attrs = attrs.OrderBy(_ => HashCode.Combine(seed, _.LogicalName)).ToList();
        }

        return new FakeMetadata([
            new CrmEntityMetadata
            {
                LogicalName = "salesorder",
                EntitySetName = "salesorders",
                DisplayName = "Sales Order",
                PrimaryIdAttribute = "salesorderid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true,
                Attributes = attrs,
                Relationships = []
            }
        ]);
    }

    private static FakeMetadata RelationshipFixture() =>
        new([
            new CrmEntityMetadata
            {
                LogicalName = "salesorder",
                EntitySetName = "salesorders",
                DisplayName = "Sales Order",
                PrimaryIdAttribute = "salesorderid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true,
                Attributes =
                [
                    Attr("customerid", "Customer", CrmAttributeKind.Customer),
                    Attr("te_branchaccountid", "Branch Account", CrmAttributeKind.Lookup)
                ],
                Relationships =
                [
                    Rel("te_account_salesorder_customeraccountid", "salesorder", "account", "customerid", "accountid", "Customer Account"),
                    Rel("te_account_salesorder_branchaccountid", "salesorder", "account", "te_branchaccountid", "accountid", "Branch Account"),
                    Rel("unrelated_contact", "salesorder", "contact", "customerid", "contactid", "Contact")
                ]
            },
            new CrmEntityMetadata
            {
                LogicalName = "account",
                EntitySetName = "accounts",
                DisplayName = "Account",
                PrimaryIdAttribute = "accountid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true,
                Attributes = [Attr("accountid", "Account", CrmAttributeKind.Uniqueidentifier), Attr("name", "Account Name", CrmAttributeKind.String)],
                Relationships =
                [
                    Rel("te_account_salesorder_customeraccountid", "salesorder", "account", "customerid", "accountid", "Customer Account"),
                    Rel("te_account_salesorder_branchaccountid", "salesorder", "account", "te_branchaccountid", "accountid", "Branch Account")
                ]
            }
        ]);

    private static CrmAttributeMetadata Attr(
        string name,
        string display,
        CrmAttributeKind kind,
        bool sortable = false) =>
        new()
        {
            LogicalName = name,
            ODataPropertyName = name,
            DisplayName = display,
            Kind = kind,
            IsValidForRead = true,
            IsFilterable = true,
            IsSortable = sortable,
            AllowedOperators = AttributeKindMapper.DefaultOperators(kind)
        };

    private static CrmRelationshipMetadata Rel(
        string schema,
        string referencing,
        string referenced,
        string referencingAttr,
        string referencedAttr,
        string label) =>
        new()
        {
            SchemaName = schema,
            Kind = CrmRelationshipKind.ManyToOne,
            ReferencingEntity = referencing,
            ReferencedEntity = referenced,
            ReferencingAttribute = referencingAttr,
            ReferencedAttribute = referencedAttr,
            ReferencingAttributeDisplayName = label,
            BusinessLabel = label,
            IsApproved = true
        };

    private sealed class FakeMetadata(IReadOnlyList<CrmEntityMetadata> entities) : ICrmMetadataService
    {
        private readonly Dictionary<string, CrmEntityMetadata> _map =
            entities.ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);

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
            if (GetEntityAsync(entityLogicalName).Result is { } entity &&
                entity.TryGetAttribute(attributeLogicalName, out var attr))
            {
                return Task.FromResult(new AttributeOptionsResult
                {
                    Attribute = attr,
                    OptionsAvailable = attr.OptionSetByValue.Count > 0 || attr.OptionSetByLabel.Count > 0,
                    AlreadyCached = true
                });
            }

            return Task.FromResult(new AttributeOptionsResult
            {
                Error = $"Attribute '{attributeLogicalName}' was not found on '{entityLogicalName}'."
            });
        }

        public string GetAiCatalogSummary() => string.Empty;
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _queue;
        public int CallCount { get; private set; }
        public List<FunctionResultContent> CapturedToolResults { get; } = [];

        public ScriptedChatClient(params ChatResponse[] responses) =>
            _queue = new Queue<ChatResponse>(responses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            foreach (var fr in messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()))
            {
                CapturedToolResults.Add(fr);
            }

            return Task.FromResult(_queue.Dequeue());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingExecutor : CrmAiPoc.Api.Crm.FetchXml.IFetchXmlQueryExecutor
    {
        public int QueryCount { get; private set; }

        public Task<CrmAiPoc.Api.Crm.FetchXml.FetchXmlValidationOutcome> ValidateAsync(
            CrmAiPoc.Api.Crm.FetchXml.FetchXmlToolRequest request,
            CancellationToken cancellationToken = default)
        {
            var entity = new CrmEntityMetadata
            {
                LogicalName = "salesorder",
                EntitySetName = "salesorders",
                DisplayName = "Sales Order",
                PrimaryIdAttribute = "salesorderid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true
            };
            return Task.FromResult(CrmAiPoc.Api.Crm.FetchXml.FetchXmlValidationOutcome.Ok(
                new CrmAiPoc.Api.Crm.FetchXml.ValidatedFetchXmlQuery
                {
                    FetchXml = request.FetchXml,
                    EntityLogicalName = entity.LogicalName,
                    EntitySetName = entity.EntitySetName,
                    Entity = entity,
                    RelativeRequestUri = "salesorders?fetchXml=x",
                    Top = 5,
                    ResultMode = request.ResultMode,
                    Limitation = request.Limitation,
                    Columns =
                    [
                        new CrmAiPoc.Api.Crm.Generic.CrmTableColumn { Key = "name", Label = "Name", Kind = "string" },
                        new CrmAiPoc.Api.Crm.Generic.CrmTableColumn { Key = "totalamount", Label = "Total Amount", Kind = "money" }
                    ]
                }));
        }

        public Task<CrmAiPoc.Api.Crm.Generic.GenericCrmQueryResult> ExecuteAsync(
            CrmAiPoc.Api.Crm.FetchXml.ValidatedFetchXmlQuery query,
            CancellationToken cancellationToken = default)
        {
            QueryCount++;
            return Task.FromResult(new CrmAiPoc.Api.Crm.Generic.GenericCrmQueryResult
            {
                Entity = query.EntityLogicalName,
                Operation = CrmAiPoc.Api.Crm.Generic.CrmQueryOperation.List,
                RelativeRequestUri = query.RelativeRequestUri,
                Items = [new Dictionary<string, object?> { ["name"] = "Demo", ["totalamount"] = 100m }],
                Columns = query.Columns,
                ProviderName = "Test",
                ResultMode = "complete"
            });
        }
    }

    private sealed class StubHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
