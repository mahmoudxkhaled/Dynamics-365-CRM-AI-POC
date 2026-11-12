using System.ComponentModel;
using System.Reflection;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrmAiPoc.Tests;

/// <summary>
/// Asserts Task 3 prompt/tool guidance is present in outbound construction.
/// Does not claim Groq will obey these instructions at runtime.
/// </summary>
public sealed class DiscoveryFirstPromptGuidanceTests
{
    [Fact]
    public void System_Prompt_Requires_Discovery_First_For_Multi_Entity_Schema()
    {
        var prompt = SystemPrompt();

        Assert.Contains("discover_crm_schema FIRST", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("multi-entity", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("relationshipPairs", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not invent custom attributes", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Direct query_crm", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("single-entity", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_Prompt_Documents_From_To_Join_Direction()
    {
        var prompt = SystemPrompt();

        Assert.Contains("from = attribute on the linked entity", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("to = attribute on the immediate parent entity", prompt, StringComparison.OrdinalIgnoreCase);
        // Task 4B replaced the older "never guess among customer/branch/billing" wording with
        // metadata-driven businessLabel guidance; assert the same intent.
        Assert.Contains("businessLabel", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not interchangeable", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clarify_crm", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not guess", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_Prompt_Excludes_Unsupported_Hyphenated_Fetch_Attributes_From_Guidance()
    {
        var prompt = SystemPrompt();

        Assert.Contains("no-lock", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("output-format", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not invent unsupported root attributes", prompt, StringComparison.OrdinalIgnoreCase);
        // Examples must not emit hyphenated unsupported forms as if valid.
        Assert.DoesNotContain("no-lock=\"", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("output-format=\"", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nolock=\"", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outputformat=\"", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_Prompt_Guides_Aggregate_Aliases_And_Ordering()
    {
        var prompt = SystemPrompt();

        Assert.Contains("order alias=\"declaredAlias\"", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never order attribute=\"aliasName\"", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dategrouping", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<order alias=\"revenue\"", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not combine top with count", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_Prompt_Preserves_Four_Tools_And_Clarification_Behavior()
    {
        var prompt = SystemPrompt();

        Assert.Contains("discover_crm_schema", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("query_crm", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clarify_crm", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unsupported_crm_request", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXACTLY FOUR tools", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never ask the user to pick SchemaNames", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_Tool_Description_Explains_When_And_How_To_Use_Relationships()
    {
        var desc = GetMethodDescription(nameof(CrmAiTools.discover_crm_schema));

        Assert.Contains("BEFORE query_crm", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("multi-entity", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("relationshipPairsJson", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("from=linked entity attribute", desc, StringComparison.OrdinalIgnoreCase);
        // Task 4B shortened "to=parent entity attribute" to "to=parent" while keeping the same meaning.
        Assert.Contains("to=parent", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("businessLabel", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clarify_crm", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not guess", desc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Query_Tool_Description_Defers_To_Discovery_For_Joins()
    {
        var desc = GetMethodDescription(nameof(CrmAiTools.query_crm));

        Assert.Contains("discover_crm_schema first", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("single-entity", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never invent logical names", desc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AiFunctionFactory_Still_Exposes_All_Four_Tools()
    {
        var tools = new CrmAiTools(NullLogger<CrmAiTools>.Instance);
        var names = new[]
        {
            AIFunctionFactory.Create(tools.discover_crm_schema).Name,
            AIFunctionFactory.Create(tools.query_crm).Name,
            AIFunctionFactory.Create(tools.clarify_crm).Name,
            AIFunctionFactory.Create(tools.unsupported_crm_request).Name
        };

        Assert.Contains("discover_crm_schema", names);
        Assert.Contains("query_crm", names);
        Assert.Contains("clarify_crm", names);
        Assert.Contains("unsupported_crm_request", names);
    }

    [Fact]
    public void Outbound_History_Contains_Guidance_Without_Crm_Records()
    {
        var history = ChatService.BuildOutboundHistory(
            "Show the top 10 Sales Orders with Customer Account and Billing Contact details.");

        Assert.Equal(ChatRole.System, history[0].Role);
        Assert.Equal(ChatRole.User, history[1].Role);
        Assert.Contains("discover_crm_schema FIRST", history[0].Text!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ORD-", history[0].Text!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("totalamount\":", history[0].Text!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(history, m => m.Role == ChatRole.Assistant || m.Role == ChatRole.Tool);
    }

    [Fact]
    public void Ai_Options_Budgets_Defaults_Unchanged()
    {
        var ai = new AiOptions();
        Assert.Equal(2, ai.MaxSchemaDiscoveryCallsPerRequest);
        Assert.Equal(5, ai.MaxSchemaDiscoveryCandidatesPerConcept);
        Assert.Equal(2, ai.MaxQueryPlanCorrectionRetries);
    }

    private static string SystemPrompt()
    {
        var history = ChatService.BuildOutboundHistory("probe");
        return history[0].Text ?? string.Empty;
    }

    private static string GetMethodDescription(string methodName)
    {
        var method = typeof(CrmAiTools).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        var attr = method!.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(attr);
        return attr!.Description;
    }
}
