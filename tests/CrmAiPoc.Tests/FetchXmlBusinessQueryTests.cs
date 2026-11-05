using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class FetchXmlBusinessQueryTests
{
    private static (GenericCrmQueryBuilder Builder, CrmEntityMetadata SalesOrder) Create()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            BusinessTimeZoneId = "Africa/Cairo"
        });
        var policy = new EntityApprovalPolicy(options);
        var entity = StaticSeedCrmMetadata.CreateApprovedSeeds(policy)
            .First(e => e.LogicalName == "salesorder");
        return (new GenericCrmQueryBuilder(options), entity);
    }

    private static string DecodeFetch(string? uri)
    {
        Assert.False(string.IsNullOrWhiteSpace(uri));
        Assert.Contains("fetchXml=", uri);
        return Uri.UnescapeDataString(uri!.Split("fetchXml=", 2)[1]);
    }

    [Fact]
    public void List_GeneratesFetchXml_NotOData()
    {
        var (builder, entity) = Create();
        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.List,
                Limit = 5,
                Sort = [new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }]
            },
            entity);

        Assert.True(built.Success, built.Error);
        var fetch = DecodeFetch(built.RelativeRequestUri);
        Assert.Equal(fetch, built.FetchXml);
        Assert.Contains("<fetch", fetch);
        Assert.Contains("top=\"5\"", fetch);
        Assert.Contains("attribute=\"createdon\"", fetch);
        Assert.Contains("descending=\"true\"", fetch);
        Assert.DoesNotContain("$select=", built.RelativeRequestUri);
        Assert.DoesNotContain("$filter=", built.RelativeRequestUri);
        Assert.DoesNotContain("$top=", built.RelativeRequestUri);
        Assert.DoesNotContain("$orderby=", built.RelativeRequestUri);
    }

    [Fact]
    public void Count_GeneratesAggregateFetchXml()
    {
        var (builder, entity) = Create();
        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.Count,
                RelativeDate = new CrmRelativeDateFilter
                {
                    Field = "createdon",
                    Token = RelativeDateToken.Last30Days
                }
            },
            entity);

        Assert.True(built.Success, built.Error);
        var fetch = DecodeFetch(built.RelativeRequestUri);
        Assert.Contains("aggregate=\"true\"", fetch);
        Assert.Contains($"alias=\"{GenericCrmQueryBuilder.CountAlias}\"", fetch);
        Assert.Contains("aggregate=\"count\"", fetch);
        Assert.Contains("createdon", fetch);
        Assert.DoesNotContain("$count=", built.RelativeRequestUri);
    }

    [Fact]
    public void Filters_And_Sort_AreEncodedInFetchXml()
    {
        var (builder, entity) = Create();
        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.List,
                Filters =
                [
                    new CrmQueryFilter
                    {
                        Field = "totalamount",
                        Operator = CrmFilterOperator.Gt,
                        Value = "1000"
                    },
                    new CrmQueryFilter
                    {
                        Field = "statecode",
                        Operator = CrmFilterOperator.Eq,
                        Value = "Active"
                    }
                ],
                Sort = [new CrmQuerySort { Field = "totalamount", Direction = CrmSortDirection.Desc }],
                Limit = 10
            },
            entity);

        Assert.True(built.Success, built.Error);
        var fetch = built.FetchXml!;
        Assert.Contains("filter type=\"and\"", fetch);
        Assert.Contains("attribute=\"totalamount\"", fetch);
        Assert.Contains("operator=\"gt\"", fetch);
        Assert.Contains("attribute=\"statecode\"", fetch);
        Assert.Contains("order attribute=\"totalamount\"", fetch);
    }

    [Fact]
    public void GuidLookup_Filter_UsesLogicalAttributeName()
    {
        var (builder, entity) = Create();
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.List,
                RelatedFromEntity = "account",
                RelatedFromId = id.ToString("D"),
                Limit = 5
            },
            entity);

        Assert.True(built.Success, built.Error);
        Assert.Contains("te_customeraccountid", built.FetchXml);
        Assert.DoesNotContain("_te_customeraccountid_value", built.FetchXml);
        Assert.Contains(id.ToString("D"), built.FetchXml);
    }

    [Fact]
    public void Injection_In_FieldName_IsRejected()
    {
        var (builder, entity) = Create();
        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Filters =
                [
                    new CrmQueryFilter
                    {
                        Field = "name eq 'x') or (1 eq 1",
                        Operator = CrmFilterOperator.Eq,
                        Value = "y"
                    }
                ]
            },
            entity);

        Assert.False(built.Success);
    }

    [Fact]
    public void Aggregate_IsNotBuiltByListCountBuilder()
    {
        var (builder, entity) = Create();
        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.Aggregate,
                Aggregates =
                [
                    new CrmAggregateSpec
                    {
                        Function = CrmAggregateFunction.Count,
                        Field = "salesorderid",
                        Alias = "record_count"
                    }
                ]
            },
            entity);

        Assert.False(built.Success);
        Assert.Contains("FetchXmlAggregateBuilder", built.Error);
    }
}
