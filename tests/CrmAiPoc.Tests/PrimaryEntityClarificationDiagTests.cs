using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class PrimaryEntityClarificationDiagTests
{
    [Fact]
    public void Diagnose_SalesOrders_With_CustomerAccount_And_PrimaryContact()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            EntityAliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = ["accounts", "customers", "customer account", "customer accounts"],
                ["contact"] = ["contacts", "contact persons", "primary contact", "primary contacts"],
                ["salesorder"] = ["sales order", "sales orders", "orders"],
                ["lead"] = ["leads", "prospects"]
            }
        });
        var policy = new EntityApprovalPolicy(options);
        var entities = StaticSeedCrmMetadata.CreateApprovedSeeds(policy).ToArray();
        const string msg =
            "Show the latest 5 Sales Orders with Customer Account and Primary Contact names.";

        var mentions = CrmEntityResolver.FindMentionedEntities(msg, entities, policy);
        var result = CrmEntityResolver.Resolve(msg, null, entities, policy);

        // Force assertion message to include diagnostic context.
        Assert.True(
            result.Status == CrmEntityResolutionStatus.Resolved &&
            result.LogicalName == "salesorder",
            $"mentions=[{string.Join(",", mentions)}] status={result.Status} entity={result.LogicalName} msg={result.Message}");
    }
}
