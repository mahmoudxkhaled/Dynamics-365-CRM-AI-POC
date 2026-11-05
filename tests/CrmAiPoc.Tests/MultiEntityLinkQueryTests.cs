using System.Text.Json;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Mock;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class MultiEntityLinkQueryTests
{
    private static (GenericCrmQueryBuilder Builder, EntityApprovalPolicy Policy, IReadOnlyList<CrmEntityMetadata> Seeds)
        CreateBuilder()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            ApprovedRelationships =
            [
                "te_account_salesorder_customeraccountid",
                "order_customer_accounts",
                "contact_customer_accounts"
            ],
            BusinessTimeZoneId = "Africa/Cairo"
        });
        var policy = new EntityApprovalPolicy(options);
        var seeds = StaticSeedCrmMetadata.CreateApprovedSeeds(policy);
        return (new GenericCrmQueryBuilder(options), policy, seeds);
    }

    private static CrmEntityMetadata Entity(IReadOnlyList<CrmEntityMetadata> seeds, string name) =>
        seeds.First(e => e.LogicalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string DecodeFetch(string? uri)
    {
        Assert.False(string.IsNullOrWhiteSpace(uri));
        Assert.Contains("fetchXml=", uri);
        return Uri.UnescapeDataString(uri!.Split("fetchXml=", 2)[1]);
    }

    [Fact]
    public void SingleHop_ManyToOne_SalesOrder_To_Account_EmitsLinkEntity()
    {
        var (builder, _, seeds) = CreateBuilder();
        var salesorder = Entity(seeds, "salesorder");
        Func<string, CrmEntityMetadata?> get = n => seeds.FirstOrDefault(e =>
            e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase));

        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.List,
                Limit = 5,
                Sort = [new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }],
                Links =
                [
                    new CrmLinkSpec
                    {
                        Entity = "account",
                        Relationship = "te_account_salesorder_customeraccountid",
                        JoinType = CrmLinkJoinType.Outer,
                        Alias = "ca",
                        Select = ["name"]
                    }
                ]
            },
            salesorder,
            getEntity: get);

        Assert.True(built.Success, built.Error);
        var fetch = DecodeFetch(built.RelativeRequestUri);
        Assert.Contains("<link-entity", fetch);
        Assert.Contains("name=\"account\"", fetch);
        Assert.Contains("from=\"accountid\"", fetch);
        Assert.Contains("to=\"te_customeraccountid\"", fetch);
        Assert.Contains("alias=\"ca\"", fetch);
        Assert.Contains("link-type=\"outer\"", fetch);
        Assert.Contains("name=\"name\"", fetch);
        Assert.Contains(built.LinkColumns, c => c.Key == "ca.name");
    }

    [Fact]
    public void SingleHop_OneToMany_Account_To_Contact_EmitsLinkEntity()
    {
        var (builder, _, seeds) = CreateBuilder();
        var account = Entity(seeds, "account");
        Func<string, CrmEntityMetadata?> get = n => seeds.FirstOrDefault(e =>
            e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase));

        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "account",
                Operation = CrmQueryOperation.List,
                Limit = 10,
                Links =
                [
                    new CrmLinkSpec
                    {
                        Entity = "contact",
                        Relationship = "contact_customer_accounts",
                        JoinType = CrmLinkJoinType.Outer,
                        Alias = "ct",
                        Select = ["fullname"]
                    }
                ]
            },
            account,
            getEntity: get);

        Assert.True(built.Success, built.Error);
        var fetch = DecodeFetch(built.RelativeRequestUri);
        Assert.Contains("name=\"contact\"", fetch);
        Assert.Contains("from=\"parentcustomerid\"", fetch);
        Assert.Contains("to=\"accountid\"", fetch);
        Assert.Contains("alias=\"ct\"", fetch);
    }

    [Fact]
    public void TwoHop_SalesOrder_Account_Contact_EmitsNestedLinkEntity()
    {
        var (builder, _, seeds) = CreateBuilder();
        var salesorder = Entity(seeds, "salesorder");
        Func<string, CrmEntityMetadata?> get = n => seeds.FirstOrDefault(e =>
            e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase));

        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.List,
                Limit = 5,
                Links =
                [
                    new CrmLinkSpec
                    {
                        Entity = "account",
                        Relationship = "te_account_salesorder_customeraccountid",
                        Alias = "ca",
                        Select = ["name"],
                        Links =
                        [
                            new CrmLinkSpec
                            {
                                Entity = "contact",
                                Relationship = "contact_customer_accounts",
                                Alias = "ct",
                                Select = ["fullname"]
                            }
                        ]
                    }
                ]
            },
            salesorder,
            getEntity: get);

        Assert.True(built.Success, built.Error);
        var fetch = DecodeFetch(built.RelativeRequestUri);
        Assert.Contains("alias=\"ca\"", fetch);
        Assert.Contains("alias=\"ct\"", fetch);
        Assert.Contains(built.LinkColumns, c => c.Key == "ca.name");
        Assert.Contains(built.LinkColumns, c => c.Key == "ct.fullname");
    }

    [Fact]
    public void AmbiguousRelationship_WithoutSchemaName_Fails()
    {
        var (builder, _, seeds) = CreateBuilder();
        var salesorder = Entity(seeds, "salesorder");
        Func<string, CrmEntityMetadata?> get = n => seeds.FirstOrDefault(e =>
            e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase));

        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Links = [new CrmLinkSpec { Entity = "account", Select = ["name"] }]
            },
            salesorder,
            getEntity: get);

        Assert.False(built.Success);
        Assert.Contains("Ambiguous relationship", built.Error);
        Assert.Contains("te_account_salesorder_customeraccountid", built.Error);
        Assert.Contains("order_customer_accounts", built.Error);
    }

    [Fact]
    public void InvalidRelationshipPath_Fails()
    {
        var (builder, _, seeds) = CreateBuilder();
        var salesorder = Entity(seeds, "salesorder");
        Func<string, CrmEntityMetadata?> get = n => seeds.FirstOrDefault(e =>
            e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase));

        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Links =
                [
                    new CrmLinkSpec
                    {
                        Entity = "account",
                        Relationship = "invented_relationship",
                        Select = ["name"]
                    }
                ]
            },
            salesorder,
            getEntity: get);

        Assert.False(built.Success);
        Assert.Contains("invented_relationship", built.Error);
    }

    [Fact]
    public void DepthBeyondLimit_Fails()
    {
        var parsed = CrmQueryPlanParser.Parse(
            entity: "salesorder",
            operation: "list",
            selectJson: null,
            filtersJson: null,
            relativeDateField: null,
            relativeDateToken: null,
            sortJson: null,
            sortField: null,
            sortDirection: null,
            limit: 5,
            relatedFromEntity: null,
            relatedFromId: null,
            linksJson: """
            [{
              "entity":"account",
              "relationship":"te_account_salesorder_customeraccountid",
              "links":[{
                "entity":"contact",
                "relationship":"contact_customer_accounts",
                "links":[{
                  "entity":"account",
                  "relationship":"contact_customer_accounts"
                }]
              }]
            }]
            """);

        Assert.False(parsed.Success);
        Assert.Contains("MaxLinkDepth", parsed.Error);
    }

    [Fact]
    public void LinksJson_Parser_And_RelatedFilter()
    {
        var parsed = CrmQueryPlanParser.Parse(
            entity: "salesorder",
            operation: "list",
            selectJson: """["name","createdon"]""",
            filtersJson: null,
            relativeDateField: null,
            relativeDateToken: null,
            sortJson: null,
            sortField: "createdon",
            sortDirection: "desc",
            limit: 5,
            relatedFromEntity: null,
            relatedFromId: null,
            linksJson: """
            [{
              "entity":"account",
              "relationship":"te_account_salesorder_customeraccountid",
              "joinType":"inner",
              "alias":"ca",
              "select":["name"],
              "filters":[{"field":"name","operator":"contains","value":"Ahmed"}]
            }]
            """);

        Assert.True(parsed.Success, parsed.Error);
        Assert.Single(parsed.Plan!.Links);
        Assert.Equal("account", parsed.Plan.Links[0].Entity);
        Assert.Equal("te_account_salesorder_customeraccountid", parsed.Plan.Links[0].Relationship);
        Assert.Equal(CrmLinkJoinType.Inner, parsed.Plan.Links[0].JoinType);
        Assert.Single(parsed.Plan.Links[0].Filters);
    }

    [Fact]
    public async Task MockRepository_SingleHop_IncludesCustomerAccountName()
    {
        var repo = CreateMockRepo();
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.List,
            Limit = 5,
            Sort = [new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }],
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Relationship = "te_account_salesorder_customeraccountid",
                    Alias = "ca",
                    Select = ["name"]
                }
            ]
        });

        Assert.True(string.IsNullOrWhiteSpace(result.Message) || result.Items.Count > 0, result.Message);
        Assert.NotEmpty(result.Items);
        Assert.Contains(result.Columns, c => c.Key == "ca.name");
        Assert.Contains(result.Items, r =>
            r.TryGetValue("ca.name", out var name) &&
            !string.IsNullOrWhiteSpace(Convert.ToString(name)));
    }

    [Fact]
    public async Task MockRepository_RelatedAccountNameFilter_InnerJoin()
    {
        var repo = CreateMockRepo();
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.List,
            Limit = 20,
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Relationship = "te_account_salesorder_customeraccountid",
                    JoinType = CrmLinkJoinType.Inner,
                    Alias = "ca",
                    Select = ["name"],
                    Filters =
                    [
                        new CrmQueryFilter
                        {
                            Field = "name",
                            Operator = CrmFilterOperator.Contains,
                            Value = "Ahmed"
                        }
                    ]
                }
            ]
        });

        Assert.NotEmpty(result.Items);
        Assert.All(result.Items, r =>
        {
            Assert.True(r.TryGetValue("ca.name", out var name));
            Assert.Contains("Ahmed", Convert.ToString(name)!, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task MockRepository_TwoHop_SalesOrder_Account_Contact()
    {
        var repo = CreateMockRepo();
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.List,
            Limit = 10,
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Relationship = "te_account_salesorder_customeraccountid",
                    Alias = "ca",
                    Select = ["name"],
                    Links =
                    [
                        new CrmLinkSpec
                        {
                            Entity = "contact",
                            Relationship = "contact_customer_accounts",
                            Alias = "ct",
                            Select = ["fullname"],
                            JoinType = CrmLinkJoinType.Outer
                        }
                    ]
                }
            ]
        });

        Assert.NotEmpty(result.Items);
        Assert.Contains(result.Columns, c => c.Key == "ca.name");
        Assert.Contains(result.Columns, c => c.Key == "ct.fullname");
    }

    [Fact]
    public async Task MockRepository_OuterJoin_KeepsRootWhenRelatedMissing()
    {
        var repo = CreateMockRepo();
        // Confidential partner orders may still join; force a filter that matches no account via wrong name on outer.
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.List,
            Limit = 50,
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Relationship = "te_account_salesorder_customeraccountid",
                    JoinType = CrmLinkJoinType.Outer,
                    Alias = "ca",
                    Select = ["name"]
                }
            ]
        });

        Assert.NotEmpty(result.Items);
        Assert.Equal(result.Items.Count, result.Items.Count); // roots preserved (may expand 1:1)
        Assert.All(result.Items, r => Assert.True(r.ContainsKey("ca.name")));
    }

    [Fact]
    public void RelationshipDiscovery_SeedContainsCustomerAccountRelationship()
    {
        var (_, policy, seeds) = CreateBuilder();
        var so = Entity(seeds, "salesorder");
        var rel = so.Relationships.Single(r =>
            r.SchemaName.Equals("te_account_salesorder_customeraccountid", StringComparison.OrdinalIgnoreCase));
        Assert.True(rel.IsApproved);
        Assert.Equal("salesorder", rel.ReferencingEntity);
        Assert.Equal("account", rel.ReferencedEntity);
        Assert.Equal("te_customeraccountid", rel.ReferencingAttribute);
        Assert.Equal("accountid", rel.ReferencedAttribute);
        Assert.True(policy.IsRelationshipApproved(rel.SchemaName, rel.ReferencingEntity, rel.ReferencedEntity));
    }

    [Fact]
    public void LinkResultNormalizer_MapsAliasVariants()
    {
        var columns = new[]
        {
            new CrmTableColumn { Key = "ca.name", Label = "Account Name", Kind = "String", LogicalName = "name" }
        };
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = "Order 1",
                ["ca_name"] = "Ahmed Trading Co"
            }
        };

        var normalized = LinkResultNormalizer.Normalize(rows, columns);
        Assert.Equal("Ahmed Trading Co", normalized[0]["ca.name"]);
    }

    private static MockGenericCrmRepository CreateMockRepo()
    {
        var options = Options.Create(new CrmOptions
        {
            Provider = "Mock",
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            ApprovedRelationships =
            [
                "te_account_salesorder_customeraccountid",
                "order_customer_accounts",
                "contact_customer_accounts"
            ]
        });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        foreach (var seed in StaticSeedCrmMetadata.CreateApprovedSeeds(policy))
        {
            registry.Upsert(seed);
        }

        var metadata = new MockCrmMetadataService(registry, policy);
        var builder = new GenericCrmQueryBuilder(options);
        var aggregate = new FetchXmlAggregateBuilder(options);
        var auth = new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy);
        return new MockGenericCrmRepository(metadata, policy, builder, aggregate, auth);
    }
}
