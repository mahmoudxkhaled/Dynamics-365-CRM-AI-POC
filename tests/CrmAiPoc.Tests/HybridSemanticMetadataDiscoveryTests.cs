using System.Diagnostics;
using System.Text.RegularExpressions;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Discovery;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// SEM-02 unit tests: hybrid lexical + semantic metadata discovery with a fake embedder.
/// No live Ollama or Dynamics connectivity required.
/// </summary>
public sealed class HybridSemanticMetadataDiscoveryTests
{
    private const int Dims = 64;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public HybridSemanticMetadataDiscoveryTests(Xunit.Abstractions.ITestOutputHelper output) =>
        _output = output;

    [Fact]
    public async Task Exact_Logical_Name_Remains_Top1_Under_Hybrid()
    {
        var fixture = MultiEntityFixture();
        var (discovery, _) = CreateHybridDiscovery(fixture, hybridEnabled: true);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = ["totalamount"]
        });

        Assert.True(result.Success, result.Error);
        var field = Assert.Single(result.Result!.Fields);
        Assert.Equal("totalamount", field.Candidates[0].LogicalName);
        Assert.Equal("alpha_order", field.Entity);
    }

    [Fact]
    public async Task Display_Name_Match_Remains_Strong()
    {
        var (discovery, _) = CreateHybridDiscovery(MultiEntityFixture(), hybridEnabled: true);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = ["Total Amount"]
        });

        Assert.Equal("totalamount", result.Result!.Fields[0].Candidates[0].LogicalName);
    }

    [Fact]
    public async Task Natural_Language_Paraphrase_Improved_By_Hybrid()
    {
        // Lexical alone struggles: paraphrase shares few tokens with logical/display names.
        // Semantic bag-of-words embedder aligns description text with the query.
        var entities = new FakeMetadata([
            new CrmEntityMetadata
            {
                LogicalName = "alpha_order",
                EntitySetName = "alpha_orders",
                DisplayName = "Alpha Order",
                PrimaryIdAttribute = "alpha_orderid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true,
                Attributes =
                [
                    Attr("name", "Name", CrmAttributeKind.String),
                    Attr("createdon", "Created On", CrmAttributeKind.DateTime),
                    Attr(
                        "totalamount",
                        "Order Value",
                        CrmAttributeKind.Money,
                        description: "Monetary sum representing the commercial worth of this order"),
                    Attr("statuscode", "Status", CrmAttributeKind.Integer),
                    Attr("customernumber", "Customer Number", CrmAttributeKind.String)
                ]
            }
        ]);

        var lexicalOnly = CreateLexicalDiscovery(entities);
        var (hybrid, embedder) = CreateHybridDiscovery(entities, hybridEnabled: true);

        const string concept = "commercial monetary worth of the purchase";
        var lex = await lexicalOnly.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = [concept]
        });
        var hyb = await hybrid.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = [concept]
        });

        Assert.True(embedder.BatchCallCount >= 1 || embedder.SingleCallCount >= 1);

        var lexTop = lex.Result!.Fields.FirstOrDefault()?.Candidates.FirstOrDefault()?.LogicalName;
        var hybTop = hyb.Result!.Fields.FirstOrDefault()?.Candidates.FirstOrDefault()?.LogicalName;

        // Hybrid should surface totalamount via description embedding when lexical misses or ranks poorly.
        Assert.Equal("totalamount", hybTop);
        Assert.True(
            lexTop is null || lexTop != "totalamount" ||
            (lex.Result.Fields[0].LowConfidence && !hyb.Result!.Fields[0].LowConfidence) ||
            hyb.Result!.Fields[0].Candidates[0].Score >=
            (lex.Result.Fields.FirstOrDefault()?.Candidates.FirstOrDefault()?.Score ?? 0),
            $"Expected hybrid to resolve paraphrase. LexTop={lexTop}, HybTop={hybTop}");
    }

    [Fact]
    public async Task Compound_Entity_And_Attribute_Concept_Respects_Scope()
    {
        var (discovery, _) = CreateHybridDiscovery(MultiEntityFixture(), hybridEnabled: true);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = ["alpha order revenue amount"]
        });

        var field = Assert.Single(result.Result!.Fields);
        Assert.Equal("alpha_order", field.Entity);
        Assert.Equal("totalamount", field.Candidates[0].LogicalName);
        Assert.DoesNotContain(field.Candidates, c => c.LogicalName == "revenue");
    }

    [Fact]
    public async Task Similar_Attribute_Names_Across_Entities_Keep_Ownership()
    {
        var (discovery, _) = CreateHybridDiscovery(MultiEntityFixture(), hybridEnabled: true);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["beta_account"],
            Concepts = ["name"]
        });

        var field = Assert.Single(result.Result!.Fields);
        Assert.Equal("beta_account", field.Entity);
        Assert.Equal("name", field.Candidates[0].LogicalName);
        Assert.All(field.Candidates, c => Assert.True(c.Score >= 0));
    }

    [Fact]
    public async Task Custom_Naming_Convention_Is_Discoverable()
    {
        var (discovery, _) = CreateHybridDiscovery(MultiEntityFixture(), hybridEnabled: true);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["te_widget"],
            Concepts = ["te_prioritycode"]
        });

        Assert.Equal("te_prioritycode", result.Result!.Fields[0].Candidates[0].LogicalName);
        Assert.Equal("te_widget", result.Result.Fields[0].Entity);
    }

    [Fact]
    public async Task Relationship_Discovery_Still_Works_With_Hybrid_Enabled()
    {
        var (discovery, _) = CreateHybridDiscovery(MultiEntityFixture(), hybridEnabled: true);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order", "beta_account"],
            RelationshipPairs = [("alpha_order", "beta_account")]
        });

        var rel = Assert.Single(result.Result!.Relationships);
        Assert.Contains(rel.Candidates, c => c.SchemaName == "te_beta_account_alpha_order_customerid");
    }

    [Fact]
    public async Task Ambiguous_And_Low_Confidence_Surfaced()
    {
        var (discovery, _) = CreateHybridDiscovery(MultiEntityFixture(), hybridEnabled: true);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = ["amount"]
        });

        Assert.NotEmpty(result.Result!.Fields);
        Assert.True(result.Result.Fields[0].Candidates.Count >= 2);
    }

    [Fact]
    public async Task Missing_Attribute_Reported_Unresolved()
    {
        var (discovery, _) = CreateHybridDiscovery(MultiEntityFixture(), hybridEnabled: true);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = ["quantum flux capacitor rating"]
        });

        Assert.Contains("quantum flux capacitor rating", result.Result!.UnresolvedConcepts);
        Assert.Empty(result.Result.Fields);
    }

    [Fact]
    public async Task Entity_Scope_Does_Not_Substitute_Unrelated_Entity_Attributes()
    {
        var (discovery, _) = CreateHybridDiscovery(MultiEntityFixture(), hybridEnabled: true);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = ["account number"]
        });

        if (result.Result!.Fields.Count > 0)
        {
            Assert.All(result.Result.Fields, f => Assert.Equal("alpha_order", f.Entity));
            Assert.DoesNotContain(
                result.Result.Fields.SelectMany(f => f.Candidates),
                c => c.LogicalName.Equals("accountnumber", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Embedding_Index_Is_Reused_Across_Concepts()
    {
        var (discovery, embedder) = CreateHybridDiscovery(MultiEntityFixture(), hybridEnabled: true);

        await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = ["totalamount", "name"]
        });

        // Index build uses bounded batches (+ optional model warmup); queries use EmbedAsync.
        Assert.True(embedder.BatchCallCount >= 1);
        Assert.Equal(2, embedder.SingleCallCount);
    }

    [Fact]
    public async Task Ollama_Unavailable_Falls_Back_To_Lexical()
    {
        var fixture = MultiEntityFixture();
        var failing = new FakeEmbeddingClient(Dims) { ThrowOnEmbed = true };
        var (discovery, _) = CreateHybridDiscovery(fixture, hybridEnabled: true, embedder: failing);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = ["Total Amount"]
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal("totalamount", result.Result!.Fields[0].Candidates[0].LogicalName);
        Assert.Contains(result.Result.Notes, n => n.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Dimension_Mismatch_Falls_Back_To_Lexical()
    {
        var fixture = MultiEntityFixture();
        var mismatched = new FakeEmbeddingClient(Dims) { ForceWrongDimensions = true };
        var (discovery, _) = CreateHybridDiscovery(
            fixture,
            hybridEnabled: true,
            embedder: mismatched,
            expectedDimensions: Dims);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = ["name"]
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal("name", result.Result!.Fields[0].Candidates[0].LogicalName);
    }

    [Fact]
    public async Task Feature_Flag_Disabled_Uses_Lexical_Only()
    {
        var (discovery, embedder) = CreateHybridDiscovery(MultiEntityFixture(), hybridEnabled: false);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = ["totalamount"]
        });

        Assert.Equal("totalamount", result.Result!.Fields[0].Candidates[0].LogicalName);
        Assert.Equal(0, embedder.BatchCallCount);
        Assert.Equal(0, embedder.SingleCallCount);
    }

    [Fact]
    public async Task FocusConcept_Removes_Entity_Tokens_Generically()
    {
        var scope = new[]
        {
            new CrmSchemaDiscoveryService.ScopedEntity(new CrmEntityMetadata
            {
                LogicalName = "alpha_order",
                EntitySetName = "alpha_orders",
                DisplayName = "Alpha Order",
                PrimaryIdAttribute = "alpha_orderid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true
            })
        };

        var focused = HybridMetadataRanker.FocusConceptAwayFromEntityNames(
            "alpha order total amount",
            scope);

        Assert.DoesNotContain("alpha", focused, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("order", focused, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("total", focused, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("amount", focused, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SplitTechnicalIdentifier_Is_Generic()
    {
        Assert.Equal("te priority code", MetadataDocumentBuilder.SplitTechnicalIdentifier("te_priorityCode"));
        Assert.Equal("total amount", MetadataDocumentBuilder.SplitTechnicalIdentifier("totalAmount"));
    }

    [Fact]
    public async Task Lexical_Vs_Hybrid_Benchmark_Report()
    {
        var fixture = BenchmarkFixture();
        var scenarios = BenchmarkScenarios();

        var lexical = CreateLexicalDiscovery(fixture);
        var (hybrid, embedder) = CreateHybridDiscovery(fixture, hybridEnabled: true);

        var lexHitsTop1 = 0;
        var hybHitsTop1 = 0;
        var lexHitsTop3 = 0;
        var hybHitsTop3 = 0;
        var lexFalsePos = 0;
        var hybFalsePos = 0;
        long lexMs = 0;
        long hybMs = 0;
        var lexChars = 0;
        var hybChars = 0;

        foreach (var s in scenarios)
        {
            var sw = Stopwatch.StartNew();
            var lex = await lexical.DiscoverAsync(new CrmSchemaDiscoveryRequest
            {
                Entities = s.Entities,
                Concepts = [s.Concept]
            });
            sw.Stop();
            lexMs += sw.ElapsedMilliseconds;
            lexChars += lex.Result?.ResponseCharacters ?? lex.Json.Length;

            sw.Restart();
            var hyb = await hybrid.DiscoverAsync(new CrmSchemaDiscoveryRequest
            {
                Entities = s.Entities,
                Concepts = [s.Concept]
            });
            sw.Stop();
            hybMs += sw.ElapsedMilliseconds;
            hybChars += hyb.Result?.ResponseCharacters ?? hyb.Json.Length;

            ScoreScenario(lex.Result, s, ref lexHitsTop1, ref lexHitsTop3, ref lexFalsePos);
            ScoreScenario(hyb.Result, s, ref hybHitsTop1, ref hybHitsTop3, ref hybFalsePos);
        }

        var n = scenarios.Count;
        _output.WriteLine(
            "SEM-02 BENCHMARK n={0} | Lexical Top1={1}/{0} Top3={2}/{0} FP={3} avgMs={4:F1} avgChars={5} | " +
            "Hybrid Top1={6}/{0} Top3={7}/{0} FP={8} avgMs={9:F1} avgChars={10} | EmbedBatch={11} EmbedQuery={12}",
            n,
            lexHitsTop1,
            lexHitsTop3,
            lexFalsePos,
            lexMs / (double)n,
            lexChars / n,
            hybHitsTop1,
            hybHitsTop3,
            hybFalsePos,
            hybMs / (double)n,
            hybChars / n,
            embedder.BatchCallCount,
            embedder.SingleCallCount);

        Assert.True(hybHitsTop1 >= lexHitsTop1, $"Hybrid Top-1 ({hybHitsTop1}) should be >= lexical ({lexHitsTop1}).");
        Assert.True(hybHitsTop3 >= lexHitsTop3, $"Hybrid Top-3 ({hybHitsTop3}) should be >= lexical ({lexHitsTop3}).");
        Assert.True(embedder.BatchCallCount >= 1, "Index should embed at least once for the fixture.");
    }

    private static void ScoreScenario(
        CrmSchemaDiscoveryResult? result,
        BenchmarkScenario scenario,
        ref int top1,
        ref int top3,
        ref int falsePositives)
    {
        if (scenario.ExpectUnresolved)
        {
            if (result is not null &&
                result.UnresolvedConcepts.Any(c => c.Equals(scenario.Concept, StringComparison.OrdinalIgnoreCase)))
            {
                top1++;
                top3++;
            }
            else if (result?.Fields.Count > 0)
            {
                falsePositives++;
            }

            return;
        }

        var candidates = result?.Fields.FirstOrDefault()?.Candidates ?? Array.Empty<CrmSchemaFieldCandidate>();
        var names = candidates.Select(c => c.LogicalName).ToArray();
        if (names.Length > 0 &&
            names[0].Equals(scenario.ExpectedLogicalName, StringComparison.OrdinalIgnoreCase) &&
            (result!.Fields[0].Entity.Equals(scenario.ExpectedEntity, StringComparison.OrdinalIgnoreCase)))
        {
            top1++;
            top3++;
            return;
        }

        if (names.Take(3).Any(n => n.Equals(scenario.ExpectedLogicalName, StringComparison.OrdinalIgnoreCase)) &&
            result!.Fields[0].Entity.Equals(scenario.ExpectedEntity, StringComparison.OrdinalIgnoreCase))
        {
            top3++;
            return;
        }

        if (names.Length > 0)
        {
            falsePositives++;
        }
    }

    private static (CrmSchemaDiscoveryService Discovery, FakeEmbeddingClient Embedder) CreateHybridDiscovery(
        FakeMetadata metadata,
        bool hybridEnabled,
        FakeEmbeddingClient? embedder = null,
        int expectedDimensions = Dims)
    {
        embedder ??= new FakeEmbeddingClient(expectedDimensions);
        var crmOptions = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["alpha_order", "beta_account", "te_widget", "gamma_case"]
        });
        var policy = new EntityApprovalPolicy(crmOptions);
        var auth = new CrmAuthorizationService(crmOptions, NullLogger<CrmAuthorizationService>.Instance, policy);
        var semantic = Options.Create(new SemanticMetadataOptions
        {
            EnableHybridRetrieval = hybridEnabled,
            LexicalWeight = 0.55,
            SemanticWeight = 0.45,
            MinSemanticSimilarity = 0.28f,
            SemanticCandidatePoolSize = 40,
            EnableDiskCache = false
        });
        var ollama = Options.Create(new OllamaEmbeddingOptions
        {
            EmbeddingModel = "fake-embed",
            ExpectedDimensions = expectedDimensions,
            TimeoutSeconds = 5
        });
        var index = new InMemorySemanticMetadataIndex(semantic, ollama, NullLogger<InMemorySemanticMetadataIndex>.Instance);
        var ranker = new HybridMetadataRanker(
            metadata,
            policy,
            embedder,
            index,
            semantic,
            ollama,
            NullLogger<HybridMetadataRanker>.Instance);
        var discovery = new CrmSchemaDiscoveryService(
            metadata,
            policy,
            auth,
            NullLogger<CrmSchemaDiscoveryService>.Instance,
            ranker);
        return (discovery, embedder);
    }

    private static CrmSchemaDiscoveryService CreateLexicalDiscovery(FakeMetadata metadata)
    {
        var crmOptions = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["alpha_order", "beta_account", "te_widget", "gamma_case"]
        });
        var policy = new EntityApprovalPolicy(crmOptions);
        return new CrmSchemaDiscoveryService(
            metadata,
            policy,
            new CrmAuthorizationService(crmOptions, NullLogger<CrmAuthorizationService>.Instance, policy),
            NullLogger<CrmSchemaDiscoveryService>.Instance);
    }

    private static FakeMetadata MultiEntityFixture() =>
        new([
            new CrmEntityMetadata
            {
                LogicalName = "alpha_order",
                EntitySetName = "alpha_orders",
                DisplayName = "Alpha Order",
                PrimaryIdAttribute = "alpha_orderid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true,
                Attributes =
                [
                    Attr("name", "Name", CrmAttributeKind.String),
                    Attr("totalamount", "Total Amount", CrmAttributeKind.Money, description: "Order revenue amount"),
                    Attr("freightamount", "Freight Amount", CrmAttributeKind.Money),
                    Attr("customerid", "Customer", CrmAttributeKind.Lookup)
                ],
                Relationships =
                [
                    Rel(
                        "te_beta_account_alpha_order_customerid",
                        "alpha_order",
                        "beta_account",
                        "customerid",
                        "beta_accountid",
                        "Customer Account")
                ]
            },
            new CrmEntityMetadata
            {
                LogicalName = "beta_account",
                EntitySetName = "beta_accounts",
                DisplayName = "Beta Account",
                PrimaryIdAttribute = "beta_accountid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true,
                Attributes =
                [
                    Attr("name", "Account Name", CrmAttributeKind.String),
                    Attr("accountnumber", "Account Number", CrmAttributeKind.String),
                    Attr("revenue", "Annual Revenue", CrmAttributeKind.Money)
                ],
                Relationships =
                [
                    Rel(
                        "te_beta_account_alpha_order_customerid",
                        "alpha_order",
                        "beta_account",
                        "customerid",
                        "beta_accountid",
                        "Customer Account")
                ]
            },
            new CrmEntityMetadata
            {
                LogicalName = "te_widget",
                EntitySetName = "te_widgets",
                DisplayName = "Widget",
                PrimaryIdAttribute = "te_widgetid",
                PrimaryNameAttribute = "te_name",
                IsApprovedForAi = true,
                Attributes =
                [
                    Attr("te_name", "Widget Name", CrmAttributeKind.String),
                    Attr("te_prioritycode", "Priority", CrmAttributeKind.Integer)
                ]
            }
        ]);

    private static FakeMetadata BenchmarkFixture()
    {
        var multi = MultiEntityFixture();
        var gamma = new CrmEntityMetadata
        {
            LogicalName = "gamma_case",
            EntitySetName = "gamma_cases",
            DisplayName = "Gamma Case",
            PrimaryIdAttribute = "gamma_caseid",
            PrimaryNameAttribute = "title",
            IsApprovedForAi = true,
            Attributes =
            [
                Attr("title", "Title", CrmAttributeKind.String),
                Attr(
                    "severityscore",
                    "Impact Level",
                    CrmAttributeKind.Integer,
                    description: "Numeric measure of how critical or urgent the case is"),
                Attr("name", "Internal Name", CrmAttributeKind.String)
            ]
        };

        var all = multi.GetApprovedEntitiesAsync().Result.ToList();
        all.Add(gamma);
        return new FakeMetadata(all);
    }

    private static IReadOnlyList<BenchmarkScenario> BenchmarkScenarios() =>
    [
        new("totalamount", ["alpha_order"], "alpha_order", "totalamount"),
        new("Total Amount", ["alpha_order"], "alpha_order", "totalamount"),
        new("order revenue amount", ["alpha_order"], "alpha_order", "totalamount"),
        new("commercial monetary worth of this order", ["alpha_order"], "alpha_order", "totalamount"),
        new("name", ["beta_account"], "beta_account", "name"),
        new("te_prioritycode", ["te_widget"], "te_widget", "te_prioritycode"),
        new("how critical or urgent", ["gamma_case"], "gamma_case", "severityscore"),
        new("account number", ["beta_account"], "beta_account", "accountnumber"),
        new("nonexistent field xyzzy", ["alpha_order"], "alpha_order", "", ExpectUnresolved: true),
        new("alpha order freight", ["alpha_order"], "alpha_order", "freightamount")
    ];

    private static CrmAttributeMetadata Attr(
        string name,
        string display,
        CrmAttributeKind kind,
        string? description = null) =>
        new()
        {
            LogicalName = name,
            ODataPropertyName = name,
            DisplayName = display,
            Description = description,
            Kind = kind,
            IsValidForRead = true,
            IsFilterable = true,
            IsSortable = kind is CrmAttributeKind.Money or CrmAttributeKind.Integer or CrmAttributeKind.Decimal,
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

    private sealed record BenchmarkScenario(
        string Concept,
        string[] Entities,
        string ExpectedEntity,
        string ExpectedLogicalName,
        bool ExpectUnresolved = false);

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

    /// <summary>
    /// Deterministic bag-of-words embedder for unit tests (no network).
    /// </summary>
    private sealed class FakeEmbeddingClient(int dimensions) : IOllamaEmbeddingClient
    {
        private static readonly Regex TokenSplit = new(@"[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public int BatchCallCount { get; private set; }
        public int SingleCallCount { get; private set; }
        public bool ThrowOnEmbed { get; init; }
        public bool ForceWrongDimensions { get; init; }

        public Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken = default)
        {
            SingleCallCount++;
            if (ThrowOnEmbed)
            {
                throw new HttpRequestException("Simulated Ollama unavailable");
            }

            if (ForceWrongDimensions)
            {
                throw new InvalidOperationException(
                    $"Ollama embedding dimension mismatch: expected {dimensions}, got {dimensions + 7}.");
            }

            return Task.FromResult(EmbedText(input, dimensions));
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default)
        {
            BatchCallCount++;
            if (ThrowOnEmbed)
            {
                throw new HttpRequestException("Simulated Ollama unavailable");
            }

            if (ForceWrongDimensions)
            {
                throw new InvalidOperationException(
                    $"Ollama embedding dimension mismatch: expected {dimensions}, got {dimensions + 7}.");
            }

            var list = new List<float[]>(inputs.Count);
            foreach (var input in inputs)
            {
                list.Add(EmbedText(input, dimensions));
            }

            return Task.FromResult<IReadOnlyList<float[]>>(list);
        }

        private static float[] EmbedText(string input, int dims)
        {
            var vector = new float[dims];
            var text = (input ?? string.Empty).ToLowerInvariant();
            // Strip nomic-style prefixes so query/document share token space.
            text = text.Replace("search_document:", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("search_query:", " ", StringComparison.OrdinalIgnoreCase);

            foreach (var token in TokenSplit.Split(text).Where(t => t.Length >= 2))
            {
                var idx = (int)(unchecked((uint)Hash(token)) % (uint)dims);
                vector[idx] += 1f;
                // Soft character n-grams for paraphrase overlap.
                if (token.Length >= 4)
                {
                    for (var i = 0; i <= token.Length - 3; i++)
                    {
                        var gram = token.Substring(i, 3);
                        var gIdx = (int)(unchecked((uint)Hash(gram)) % (uint)dims);
                        vector[gIdx] += 0.35f;
                    }
                }
            }

            // L2 normalize for stable cosine scores.
            double norm = 0;
            for (var i = 0; i < vector.Length; i++)
            {
                norm += vector[i] * vector[i];
            }

            if (norm > double.Epsilon)
            {
                var inv = (float)(1.0 / Math.Sqrt(norm));
                for (var i = 0; i < vector.Length; i++)
                {
                    vector[i] *= inv;
                }
            }

            return vector;
        }

        private static int Hash(string value)
        {
            unchecked
            {
                var hash = 23;
                foreach (var ch in value)
                {
                    hash = (hash * 31) + ch;
                }

                return hash;
            }
        }
    }
}
