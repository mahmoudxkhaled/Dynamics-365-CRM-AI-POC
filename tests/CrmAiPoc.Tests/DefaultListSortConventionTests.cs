using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>Offline checks that list default sort remains createdon desc when available.</summary>
public sealed class DefaultListSortConventionTests
{
    [Fact]
    public void List_Without_Sort_Defaults_To_CreatedOn_Desc_When_Available()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["product"],
            BusinessTimeZoneId = "Africa/Cairo"
        });
        var policy = new EntityApprovalPolicy(options);
        var entity = GenericApprovedEntityStubFactory.Create("product", policy);
        Assert.True(entity.TryGetAttribute("createdon", out var created) && created.IsSortable);

        var builder = new GenericCrmQueryBuilder(options);
        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = entity.LogicalName,
                Operation = CrmQueryOperation.List,
                Limit = 5,
                Sort = []
            },
            entity);

        Assert.True(built.Success, built.Error);
        Assert.Equal("createdon desc (default)", built.SortSummary);
    }
}
