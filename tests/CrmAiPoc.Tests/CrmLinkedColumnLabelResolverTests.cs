using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;

namespace CrmAiPoc.Tests;

public sealed class CrmLinkedColumnLabelResolverTests
{
    [Theory]
    [InlineData("Account Name (English)", "Account", "Account Name (English)")]
    [InlineData("Contact Name", "Contact", "Contact Name")]
    [InlineData("Account Name", "Account", "Account Name")]
    public void Does_Not_Duplicate_Entity_Prefix_When_DisplayName_Already_Contains_It(
        string attributeDisplayName,
        string entityDisplayName,
        string expected)
    {
        var label = CrmLinkedColumnLabelResolver.Resolve(
            attributeDisplayName,
            attributeLogicalName: "name",
            entityDisplayName,
            entityLogicalName: entityDisplayName.ToLowerInvariant());

        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData("Name", "Account", "Account Name")]
    [InlineData("Created On", "Account", "Account Created On")]
    [InlineData("Modified On", "Contact", "Contact Modified On")]
    public void Adds_Entity_Context_For_Generic_Attribute_DisplayNames(
        string attributeDisplayName,
        string entityDisplayName,
        string expected)
    {
        var label = CrmLinkedColumnLabelResolver.Resolve(
            attributeDisplayName,
            attributeLogicalName: "name",
            entityDisplayName,
            entityLogicalName: entityDisplayName.ToLowerInvariant());

        Assert.Equal(expected, label);
    }

    [Fact]
    public void Falls_Back_To_Logical_Names_When_DisplayNames_Missing()
    {
        var label = CrmLinkedColumnLabelResolver.Resolve(
            attributeDisplayName: null,
            attributeLogicalName: "name",
            entityDisplayName: null,
            entityLogicalName: "account");

        Assert.Equal("account name", label);
    }

    [Fact]
    public void Disambiguates_Same_Entity_Joins_By_Relationship_Role()
    {
        var customer = CrmLinkedColumnLabelResolver.Resolve(
            attributeDisplayName: "Account Name (English)",
            attributeLogicalName: "name",
            entityDisplayName: "Account",
            entityLogicalName: "account",
            relationshipDisplayName: "Customer Account",
            disambiguateByRelationship: true);

        var branch = CrmLinkedColumnLabelResolver.Resolve(
            attributeDisplayName: "Account Name (English)",
            attributeLogicalName: "name",
            entityDisplayName: "Account",
            entityLogicalName: "account",
            relationshipDisplayName: "Branch Account",
            disambiguateByRelationship: true);

        Assert.Equal("Customer Account — Account Name (English)", customer);
        Assert.Equal("Branch Account — Account Name (English)", branch);
        Assert.NotEqual(customer, branch);
    }

    [Fact]
    public void Relationship_Role_Not_Applied_When_Only_One_Join_To_Entity()
    {
        var label = CrmLinkedColumnLabelResolver.Resolve(
            attributeDisplayName: "Account Name (English)",
            attributeLogicalName: "name",
            entityDisplayName: "Account",
            entityLogicalName: "account",
            relationshipDisplayName: "Customer Account",
            disambiguateByRelationship: false);

        Assert.Equal("Account Name (English)", label);
    }

    [Fact]
    public void Relationship_Role_Skipped_When_Already_Present_In_Label()
    {
        var label = CrmLinkedColumnLabelResolver.Resolve(
            attributeDisplayName: "Customer Account Name",
            attributeLogicalName: "name",
            entityDisplayName: "Account",
            entityLogicalName: "account",
            relationshipDisplayName: "Customer Account",
            disambiguateByRelationship: true);

        Assert.Equal("Customer Account Name", label);
    }

    [Fact]
    public void StartsWithLabel_Requires_Word_Boundary()
    {
        Assert.True(CrmLinkedColumnLabelResolver.StartsWithLabel("Account Name", "Account"));
        Assert.False(CrmLinkedColumnLabelResolver.StartsWithLabel("Accountability Score", "Account"));
    }

    [Fact]
    public void BuildLinkColumns_Integration_Avoids_Duplicate_Prefix_And_Keeps_Alias_Keys()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CrmAiPoc.Api.Configuration.CrmOptions
        {
            ApprovedEntities = ["salesorder", "account", "contact"],
            ApprovedRelationships = []
        });
        var policy = new EntityApprovalPolicy(options);
        var seeds = StaticSeedCrmMetadata.CreateApprovedSeeds(policy);
        var byName = seeds.ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);

        // Patch account/contact name DisplayNames to match live-style redundant CRM labels.
        byName["account"] = CloneWithAttributeDisplay(byName["account"], "name", "Account Name (English)");
        byName["contact"] = CloneWithAttributeDisplay(byName["contact"], "fullname", "Contact Name");

        var builder = new GenericCrmQueryBuilder(options);
        var plan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Limit = 5,
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Relationship = "te_account_salesorder_customeraccountid",
                    Select = ["name"]
                },
                new CrmLinkSpec
                {
                    Entity = "contact",
                    Relationship = "order_customer_contacts",
                    Select = ["fullname"]
                }
            ]
        };

        // Ensure contact relationship exists on salesorder seed for this integration path.
        if (!byName["salesorder"].Relationships.Any(r =>
                r.SchemaName.Equals("order_customer_contacts", StringComparison.OrdinalIgnoreCase)))
        {
            byName["salesorder"] = withExtraRelationship(
                byName["salesorder"],
                new CrmRelationshipMetadata
                {
                    SchemaName = "order_customer_contacts",
                    Kind = CrmRelationshipKind.ManyToOne,
                    ReferencingEntity = "salesorder",
                    ReferencedEntity = "contact",
                    ReferencingAttribute = "customerid",
                    ReferencedAttribute = "contactid",
                    BusinessLabel = "Potential Customer (Contact)",
                    IsApproved = true
                });
        }

        var salesorder = byName["salesorder"];
        CrmEntityMetadata? Get(string logical) => byName.GetValueOrDefault(logical);
        var built = builder.Build(plan, salesorder, getEntity: Get);
        Assert.True(built.Success, built.Error);

        var accountCol = Assert.Single(built.LinkColumns, c => c.LogicalName == "name");
        var contactCol = Assert.Single(built.LinkColumns, c => c.LogicalName == "fullname");

        Assert.Equal("Account Name (English)", accountCol.Label);
        Assert.Equal("Contact Name", contactCol.Label);
        Assert.Contains('.', accountCol.Key);
        Assert.Contains('.', contactCol.Key);
        Assert.DoesNotContain("Account Account", accountCol.Label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Contact Contact", contactCol.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildLinkColumns_Disambiguates_Two_Account_Relationships()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CrmAiPoc.Api.Configuration.CrmOptions
        {
            ApprovedEntities = ["salesorder", "account"],
            ApprovedRelationships = []
        });
        var policy = new EntityApprovalPolicy(options);
        var seeds = StaticSeedCrmMetadata.CreateApprovedSeeds(policy);
        var byName = seeds.ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);
        byName["account"] = CloneWithAttributeDisplay(byName["account"], "name", "Account Name (English)");

        // Add branch relationship if missing.
        if (!byName["salesorder"].Relationships.Any(r =>
                r.SchemaName.Equals("te_account_salesorder_branchaccountid", StringComparison.OrdinalIgnoreCase)))
        {
            byName["salesorder"] = withExtraRelationship(
                byName["salesorder"],
                new CrmRelationshipMetadata
                {
                    SchemaName = "te_account_salesorder_branchaccountid",
                    Kind = CrmRelationshipKind.ManyToOne,
                    ReferencingEntity = "salesorder",
                    ReferencedEntity = "account",
                    ReferencingAttribute = "te_branchaccountid",
                    ReferencedAttribute = "accountid",
                    ReferencingAttributeDisplayName = "Branch Account",
                    BusinessLabel = "Branch Account",
                    IsApproved = true
                });
        }

        var builder = new GenericCrmQueryBuilder(options);
        var plan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Limit = 5,
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Relationship = "te_account_salesorder_customeraccountid",
                    Alias = "cust",
                    Select = ["name"]
                },
                new CrmLinkSpec
                {
                    Entity = "account",
                    Relationship = "te_account_salesorder_branchaccountid",
                    Alias = "branch",
                    Select = ["name"]
                }
            ]
        };

        var salesorder = byName["salesorder"];
        CrmEntityMetadata? Get(string logical) => byName.GetValueOrDefault(logical);
        var built = builder.Build(plan, salesorder, getEntity: Get);
        Assert.True(built.Success, built.Error);
        Assert.Equal(2, built.LinkColumns.Count);

        Assert.Contains(built.LinkColumns, c =>
            c.Key.StartsWith("cust.", StringComparison.OrdinalIgnoreCase) &&
            c.Label.Contains("Customer Account", StringComparison.OrdinalIgnoreCase) &&
            c.Label.Contains("Account Name (English)", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(built.LinkColumns, c =>
            c.Key.StartsWith("branch.", StringComparison.OrdinalIgnoreCase) &&
            c.Label.Contains("Branch Account", StringComparison.OrdinalIgnoreCase) &&
            c.Label.Contains("Account Name (English)", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(2, built.LinkColumns.Select(c => c.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static CrmEntityMetadata CloneWithAttributeDisplay(
        CrmEntityMetadata entity,
        string attributeLogicalName,
        string displayName)
    {
        var attrs = entity.Attributes.Select(a =>
            a.LogicalName.Equals(attributeLogicalName, StringComparison.OrdinalIgnoreCase)
                ? new CrmAttributeMetadata
                {
                    LogicalName = a.LogicalName,
                    ODataPropertyName = a.ODataPropertyName,
                    DisplayName = displayName,
                    Kind = a.Kind,
                    IsValidForRead = a.IsValidForRead,
                    IsFilterable = a.IsFilterable,
                    IsSortable = a.IsSortable,
                    IsSearchable = a.IsSearchable,
                    AllowedOperators = a.AllowedOperators,
                    Aliases = a.Aliases,
                    OptionSetByLabel = a.OptionSetByLabel,
                    OptionSetByValue = a.OptionSetByValue
                }
                : a).ToArray();

        return new CrmEntityMetadata
        {
            LogicalName = entity.LogicalName,
            EntitySetName = entity.EntitySetName,
            DisplayName = entity.DisplayName,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            IsApprovedForAi = entity.IsApprovedForAi,
            Aliases = entity.Aliases,
            Attributes = attrs,
            Relationships = entity.Relationships
        };
    }

    private static CrmEntityMetadata withExtraRelationship(
        CrmEntityMetadata entity,
        CrmRelationshipMetadata relationship) =>
        new()
        {
            LogicalName = entity.LogicalName,
            EntitySetName = entity.EntitySetName,
            DisplayName = entity.DisplayName,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            IsApprovedForAi = entity.IsApprovedForAi,
            Aliases = entity.Aliases,
            Attributes = entity.Attributes,
            Relationships = entity.Relationships.Concat([relationship]).ToArray()
        };
}
