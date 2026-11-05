using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class EnglishOnlyIntentTests
{
    private const string ComplexListQuestion =
        "Show the latest 10 active Sales Orders created in the last 6 months, including the Sales Order name, order number, creation date, total amount, Customer Account name, and the Account's Primary Contact name. Select the 10 most recent matching orders first, then sort those 10 results by total amount in descending order.";

    [Theory]
    [InlineData("كم عدد أوامر البيع؟", true)]
    [InlineData("Show sales orders for GUID 11111111-1111-1111-1111-111111111111", false)]
    [InlineData("Show Sales Orders named أحمد", true)]
    [InlineData("How many Sales Orders?", false)]
    public void Arabic_Script_Gate_Is_Narrow(string message, bool expected) =>
        Assert.Equal(expected, EnglishInputPolicy.ContainsArabicScript(message));

    [Fact]
    public void Complex_English_List_Resolves_Ai_Proposed_TwoHop_Links()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            EntityAliases = new Dictionary<string, List<string>>
            {
                ["account"] = ["accounts", "customers", "customer account", "customer accounts"],
                ["contact"] = ["contacts", "contact persons", "primary contact", "primary contacts"],
                ["salesorder"] = ["sales order", "sales orders", "orders"],
                ["lead"] = ["leads", "prospects"]
            },
            RelationshipLabels = new Dictionary<string, string>
            {
                ["te_account_salesorder_customeraccountid"] = "Customer Account",
                ["account_primary_contact"] = "Primary Contact",
                ["contact_customer_accounts"] = "Account Contacts"
            }
        });
        var policy = new EntityApprovalPolicy(options);
        var entities = StaticSeedCrmMetadata.CreateApprovedSeeds(policy).ToArray();
        var labeler = new CrmRelationshipLabeler(options);

        var resolution = CrmEntityResolver.Resolve(ComplexListQuestion, "salesorder", entities, policy);
        Assert.Equal(CrmEntityResolutionStatus.Resolved, resolution.Status);
        Assert.Equal("salesorder", resolution.LogicalName);

        var plan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.List,
            Limit = 10,
            Select = ["name", "ordernumber", "createdon", "totalamount"],
            Sort = [new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }],
            ClientSideSort = [new CrmQuerySort { Field = "totalamount", Direction = CrmSortDirection.Desc }],
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Alias = "ca",
                    Select = ["name"],
                    Links =
                    [
                        new CrmLinkSpec { Entity = "contact", Alias = "pc", Select = ["fullname"] }
                    ]
                }
            ]
        };

        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            plan, ComplexListQuestion, entities, policy, labeler, "en");
        Assert.True(string.IsNullOrWhiteSpace(attach.ClarificationMessage), attach.ClarificationMessage);
        Assert.NotEmpty(attach.Plan.Links);
        Assert.Equal("account", attach.Plan.Links[0].Entity);
        Assert.NotEmpty(attach.Plan.Links[0].Links);
        Assert.Equal("contact", attach.Plan.Links[0].Links[0].Entity);
        Assert.Contains("primary", attach.Plan.Links[0].Links[0].Relationship, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClientSide_Sorter_Reorders_Returned_Page_Only()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["name"] = "A", ["totalamount"] = 10m },
            new Dictionary<string, object?> { ["name"] = "B", ["totalamount"] = 50m },
            new Dictionary<string, object?> { ["name"] = "C", ["totalamount"] = 25m }
        };

        var sorted = ClientSideResultSorter.Apply(
            rows,
            [new CrmQuerySort { Field = "totalamount", Direction = CrmSortDirection.Desc }]);

        Assert.Equal("B", sorted[0]["name"]);
        Assert.Equal("C", sorted[1]["name"]);
        Assert.Equal("A", sorted[2]["name"]);
    }
}
