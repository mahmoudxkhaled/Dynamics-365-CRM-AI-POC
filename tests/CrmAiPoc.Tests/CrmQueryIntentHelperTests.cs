using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class CrmQueryIntentHelperTests
{
    [Theory]
    [InlineData("Show me the latest 5 accounts", "account")]
    [InlineData("Show me the latest 5 sales orders", "salesorder")]
    [InlineData("count sales orders", "salesorder")]
    [InlineData("show contacts for this account", "contact")]
    [InlineData("Show me the latest 5 leads", "lead")]
    public void Detects_Entity_From_Current_Message(string message, string expected)
    {
        Assert.Equal(expected, CrmQueryIntentHelper.DetectEntityLogicalName(message));
    }

    [Fact]
    public void DetectEntity_Uses_Configured_Aliases()
    {
        var policy = new EntityApprovalPolicy(Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            EntityAliases = new Dictionary<string, List<string>>
            {
                ["account"] = ["customers"],
                ["salesorder"] = ["sales orders"]
            }
        }));
        Assert.Equal("account", CrmQueryIntentHelper.DetectEntityLogicalName("list customers", policy));
        Assert.Null(CrmQueryIntentHelper.DetectEntityLogicalName("opportunities please", policy));
    }
}
