using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class OneToManyCardinalityTests
{
    private static (MockGenericCrmRepository Repo, EntityApprovalPolicy Policy) CreateRepo()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            Provider = "Mock"
        });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        foreach (var seed in StaticSeedCrmMetadata.CreateApprovedSeeds(policy))
        {
            registry.Upsert(seed);
        }

        var metadata = new MockCrmMetadataService(registry, policy);
        var auth = new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy);
        var builder = new GenericCrmQueryBuilder(options);
        var agg = new FetchXmlAggregateBuilder(options);
        var repo = new MockGenericCrmRepository(metadata, policy, builder, agg, auth);
        return (repo, policy);
    }

    [Fact]
    public async Task Account_To_Contact_List_May_Expand_Rows_And_Flags_Summary()
    {
        var (repo, policy) = CreateRepo();
        var options = Options.Create(new CrmOptions { ApprovedEntities = ["account", "contact", "salesorder", "lead"] });
        var account = StaticSeedCrmMetadata.CreateApprovedSeeds(policy).First(e => e.LogicalName == "account");
        var built = new GenericCrmQueryBuilder(options).Build(
            new CrmQueryPlan
            {
                Entity = "account",
                Limit = 50,
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
            },
            account,
            getEntity: n => StaticSeedCrmMetadata.CreateApprovedSeeds(policy)
                .FirstOrDefault(e => e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase)));

        Assert.True(built.Success, built.Error);
        Assert.Contains(built.AppliedFilterSummary, s =>
            s.Contains("expands-to-many", StringComparison.OrdinalIgnoreCase));

        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "account",
            Limit = 50,
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
        });

        Assert.True(result.Items.Count >= 1);
        Assert.Contains(result.Columns, c => c.Key.Contains("ct.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Aggregate_With_Links_Is_Rejected()
    {
        var options = Options.Create(new CrmOptions { ApprovedEntities = ["account", "contact"] });
        var policy = new EntityApprovalPolicy(options);
        var account = StaticSeedCrmMetadata.CreateApprovedSeeds(policy).First(e => e.LogicalName == "account");
        var built = new FetchXmlAggregateBuilder(options).Build(
            new CrmQueryPlan
            {
                Entity = "account",
                Operation = CrmQueryOperation.Aggregate,
                Aggregates =
                [
                    new CrmAggregateSpec
                    {
                        Function = CrmAggregateFunction.Count,
                        Field = "accountid",
                        Alias = "record_count"
                    }
                ],
                Links =
                [
                    new CrmLinkSpec
                    {
                        Entity = "contact",
                        Relationship = "contact_customer_accounts"
                    }
                ]
            },
            account);

        Assert.False(built.Success);
        Assert.Contains("one-to-many", built.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Count_FetchXml_Uses_Distinct_When_OneToMany_Inner_Filter()
    {
        var options = Options.Create(new CrmOptions { ApprovedEntities = ["account", "contact"] });
        var policy = new EntityApprovalPolicy(options);
        var account = StaticSeedCrmMetadata.CreateApprovedSeeds(policy).First(e => e.LogicalName == "account");
        var built = new GenericCrmQueryBuilder(options).Build(
            new CrmQueryPlan
            {
                Entity = "account",
                Operation = CrmQueryOperation.Count,
                Links =
                [
                    new CrmLinkSpec
                    {
                        Entity = "contact",
                        Relationship = "contact_customer_accounts",
                        JoinType = CrmLinkJoinType.Inner,
                        Filters =
                        [
                            new CrmQueryFilter
                            {
                                Field = "fullname",
                                Operator = CrmFilterOperator.Contains,
                                Value = "a"
                            }
                        ]
                    }
                ]
            },
            account,
            getEntity: n => StaticSeedCrmMetadata.CreateApprovedSeeds(policy)
                .FirstOrDefault(e => e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase)));

        Assert.True(built.Success, built.Error);
        Assert.Contains("distinct=\"true\"", built.FetchXml);
    }
}
