using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Live tool-calling checks against the LAN Ollama host.
/// Skipped unless RUN_LIVE_OLLAMA_TESTS=1 (does not require Dynamics CRM).
/// </summary>
public sealed class LiveOllamaToolCallingTests
{
    private static bool LiveEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_LIVE_OLLAMA_TESTS"), "1", StringComparison.Ordinal);

    private const string BaseUrl = "http://192.168.1.6:11434";
    private const string Model = "qwen3.5:4b-q4_K_M";

    [Fact]
    public async Task Ollama_Tags_Endpoint_Lists_Configured_Model()
    {
        if (!LiveEnabled)
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.GetAsync($"{BaseUrl}/api/tags");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains(Model, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tools", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Show me the latest 5 Sales Orders.", "query_crm")]
    [InlineData("Count all Sales Orders.", "query_crm")]
    [InlineData("Show Sales Orders with their customer account names.", "query_crm")]
    [InlineData("Show Sales Orders grouped by month.", "query_crm")]
    public async Task Ollama_Qwen_Emits_Expected_Tool_Call(string question, string expectedTool)
    {
        if (!LiveEnabled)
        {
            return;
        }

        using var client = CreateClient();
        var tools = CreatePlanningTools();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                """
                You are a read-only Dynamics 365 CRM query planner.
                You must call exactly one tool. Prefer query_crm for CRM reads.
                Use entity logical name salesorder for Sales Orders.
                For latest N use operation=list, sortField=createdon, sortDirection=desc, limit=N.
                For counts use operation=count.
                For joins use linksJson. For monthly grouping use operation=aggregate with groupByJson dateGrouping month.
                Never invent FetchXML or OData. Never answer with plain text when a tool applies.
                """),
            new(ChatRole.User, question)
        };

        var options = new ChatOptions
        {
            Tools = tools,
            ToolMode = ChatToolMode.RequireAny,
            Temperature = 0f
        };

        var sw = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            response = await client.GetResponseAsync(messages, options);
        }
        catch (Exception ex)
        {
            Assert.Fail(
                $"Ollama tool-calling request failed for '{question}': {ex.GetType().Name}: {ex.Message}");
            return;
        }

        sw.Stop();

        var calls = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .ToList();

        Assert.True(
            calls.Count > 0,
            $"Expected a tool call for '{question}' but got text only ({sw.ElapsedMilliseconds}ms): {Truncate(response.Text, 400)}");

        var call = calls[0];
        Assert.Equal(expectedTool, call.Name);

        var argsJson = call.Arguments is null
            ? "{}"
            : JsonSerializer.Serialize(call.Arguments);

        Assert.False(string.IsNullOrWhiteSpace(argsJson));
        Assert.DoesNotContain("sk-", argsJson, StringComparison.OrdinalIgnoreCase);
    }

    private static LazyOllamaChatClient CreateClient()
    {
        var ai = Options.Create(new AiOptions
        {
            Provider = "Ollama",
            ProviderTimeoutSeconds = 90,
            Ollama = new OllamaChatOptions
            {
                BaseUrl = BaseUrl,
                Model = Model,
                ContextSize = 16384,
                Temperature = 0,
                EnableThinking = false
            }
        });

        return new LazyOllamaChatClient(ai, NullLogger<LazyOllamaChatClient>.Instance);
    }

    private static AITool[] CreatePlanningTools() =>
    [
        AIFunctionFactory.Create(StubPlanningTools.query_crm),
        AIFunctionFactory.Create(StubPlanningTools.discover_crm_schema),
        AIFunctionFactory.Create(StubPlanningTools.clarify_crm),
        AIFunctionFactory.Create(StubPlanningTools.unsupported_crm_request)
    ];

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }

    private static class StubPlanningTools
    {
        [Description("Query approved CRM entities (list/count/aggregate).")]
        public static string query_crm(
            [Description("Entity logical name")] string entity,
            [Description("list|count|aggregate")] string operation = "list",
            string? sortField = null,
            string? sortDirection = null,
            int? limit = null,
            string? linksJson = null,
            string? groupByJson = null,
            string? aggregatesJson = null) =>
            "not-executed";

        [Description("Discover CRM schema candidates.")]
        public static string discover_crm_schema(string conceptsJson) => "not-executed";

        [Description("Ask the user for clarification.")]
        public static string clarify_crm(string message, string suggestionsJson) => "not-executed";

        [Description("Refuse unsupported CRM requests.")]
        public static string unsupported_crm_request(string reason) => "not-executed";
    }
}
