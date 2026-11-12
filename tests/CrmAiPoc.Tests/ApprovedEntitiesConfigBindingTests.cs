using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// CONF-01: ApprovedEntities must use replace binding, not ASP.NET Core index-merge across files.
/// </summary>
public sealed class ApprovedEntitiesConfigBindingTests
{
    [Fact]
    public void Default_GetSection_Merges_Array_Indices_Across_Providers()
    {
        var config = BuildMergedConfig(
            baseEntities: ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k"],
            overrideEntities: ["a", "b", "c", "d"]);

        var merged = config.GetSection("Crm:ApprovedEntities").Get<List<string>>();
        Assert.NotNull(merged);
        Assert.Equal(11, merged.Count);
    }

    [Fact]
    public void GetStringArrayReplace_Uses_Highest_Priority_Provider_Only()
    {
        var config = BuildMergedConfig(
            baseEntities: ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k"],
            overrideEntities: ["account", "contact", "salesorder", "product"]);

        var replaced = ConfigurationListBinding.GetStringArrayReplace(config, "Crm:ApprovedEntities");
        Assert.Equal(4, replaced.Count);
        Assert.Equal(["account", "contact", "salesorder", "product"], replaced);

        var provider = ConfigurationListBinding.DescribeWinningArrayProvider(config, "Crm:ApprovedEntities");
        Assert.Equal("MemoryConfigurationProvider", provider);
    }

    [Fact]
    public void EntityApprovalPolicy_Denies_Entities_Outside_Replaced_Allowlist()
    {
        var config = BuildMergedConfig(
            baseEntities: ["account", "contact", "lead", "salesorder", "opportunity", "quote", "invoice", "product", "campaign"],
            overrideEntities: ["account", "contact", "salesorder", "product"]);

        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ConfigurationListBinding.GetStringArrayReplace(config, "Crm:ApprovedEntities")
        });
        var policy = new EntityApprovalPolicy(options);

        Assert.Equal(4, policy.ApprovedLogicalNames.Count);
        Assert.True(policy.IsApproved("salesorder"));
        Assert.True(policy.IsApproved("product"));
        Assert.False(policy.IsApproved("opportunity"));
        Assert.False(policy.IsApproved("invoice"));
        Assert.False(policy.IsApproved("te_serviceorder"));

        var auth = new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy);
        Assert.False(auth.AuthorizeRead("opportunity", "query_crm").Allowed);
        Assert.True(auth.AuthorizeRead("account", "query_crm").Allowed);
    }

    [Fact]
    public void ContentHash_Changes_When_Approved_Entity_Set_Changes()
    {
        var full = new[]
        {
            Entity("alpha"), Entity("beta"), Entity("gamma"), Entity("delta")
        };
        var subset = full.Take(2).ToArray();

        var hashFull = MetadataDocumentBuilder.ComputeContentHash(full);
        var hashSubset = MetadataDocumentBuilder.ComputeContentHash(subset);

        Assert.NotEqual(hashFull, hashSubset);
    }

    private static IConfigurationRoot BuildMergedConfig(
        IReadOnlyList<string> baseEntities,
        IReadOnlyList<string> overrideEntities)
    {
        var basePairs = baseEntities
            .Select((name, i) => new KeyValuePair<string, string?>($"Crm:ApprovedEntities:{i}", name));
        var overridePairs = overrideEntities
            .Select((name, i) => new KeyValuePair<string, string?>($"Crm:ApprovedEntities:{i}", name));

        return new ConfigurationBuilder()
            .AddInMemoryCollection(basePairs)
            .AddInMemoryCollection(overridePairs)
            .Build();
    }

    private static CrmEntityMetadata Entity(string name) =>
        new()
        {
            LogicalName = name,
            EntitySetName = name + "s",
            PrimaryIdAttribute = name + "id",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes =
            [
                new CrmAttributeMetadata
                {
                    LogicalName = "name",
                    ODataPropertyName = "name",
                    Kind = CrmAttributeKind.String,
                    IsValidForRead = true
                }
            ]
        };
}
