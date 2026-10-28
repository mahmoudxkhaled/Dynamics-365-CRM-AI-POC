using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Diagnostics;
using CrmAiPoc.Api.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CrmAiPoc.Tests;

public class ExecutionInspectorTests
{
    [Fact]
    public void Trace_Store_Isolates_Concurrent_Executions()
    {
        var accessor = new ExecutionTraceAccessor();
        var store = new ExecutionTraceStore(accessor, capacity: 50);

        var a = store.Start("c1", "Show accounts", "exec-a");
        a.AddInstantStep(ExecutionSteps.HttpChatRequestReceived, ExecutionStepStatus.Success, "a");
        accessor.CurrentExecutionId = null;

        var b = store.Start("c2", "Delete all accounts", "exec-b");
        b.AddInstantStep(ExecutionSteps.AiToolSelected, ExecutionStepStatus.Blocked, "blocked");
        store.Complete("exec-b", ExecutionTraceStatus.Blocked);
        store.Complete("exec-a", ExecutionTraceStatus.Success);

        var snapA = store.GetSnapshot("exec-a")!;
        var snapB = store.GetSnapshot("exec-b")!;
        Assert.Equal(ExecutionTraceStatus.Success, snapA.Status);
        Assert.Equal(ExecutionTraceStatus.Blocked, snapB.Status);
        Assert.Contains(snapA.Steps, s => s.Name == ExecutionSteps.HttpChatRequestReceived);
        Assert.DoesNotContain(snapB.Steps, s => s.Name == ExecutionSteps.HttpChatRequestReceived);
        Assert.Contains(snapB.Steps, s => s.Status == ExecutionStepStatus.Blocked);
    }

    [Fact]
    public void Sanitizer_Redacts_Guids_Emails_And_Sensitive_Keys()
    {
        var args = TraceSanitizer.SanitizeToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "contact",
            ["relatedFromId"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            ["filtersJson"] = "[{\"field\":\"emailaddress1\",\"value\":\"secret@example.com\"}]",
            ["limit"] = 5
        });

        Assert.Equal("contact", args["entity"]?.ToString());
        Assert.Equal("[REDACTED]", args["relatedFromId"]?.ToString());
        Assert.Contains("[REDACTED_EMAIL]", args["filtersJson"]?.ToString());
        Assert.Equal(5L, Convert.ToInt64(args["limit"]));

        var path = TraceSanitizer.RedactODataPath(
            "contacts?$filter=_parentcustomerid_value eq aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee and fullname eq 'Ali'");
        Assert.Contains("[REDACTED_GUID]", path);
        Assert.Contains("'[REDACTED]'", path);
        Assert.DoesNotContain("Ali", path);
    }

    [Fact]
    public void Sanitizer_Safe_Trace_Text_Omits_Raw_Customer_Payload()
    {
        var accessor = new ExecutionTraceAccessor();
        var store = new ExecutionTraceStore(accessor);
        var trace = store.Start("c", "Show me latest accounts", "exec-safe");
        trace.SelectedTool = "query_crm";
        trace.CrmQuery = new { relativeRequestUri = "accounts?$top=5", recordCount = 5 };
        trace.AddInstantStep(ExecutionSteps.DynamicsHttpResponse, ExecutionStepStatus.Success, "rows not stored");
        store.Complete("exec-safe", ExecutionTraceStatus.Success);

        var text = TraceSanitizer.BuildSafeTraceText(store.GetSnapshot("exec-safe")!);
        Assert.Contains("query_crm", text);
        Assert.Contains("accounts?$top=5", text);
        Assert.DoesNotContain("ORD-", text);
    }

    [Fact]
    public void Tracer_Stores_Outbound_Ai_Prompt_On_Snapshot()
    {
        var accessor = new ExecutionTraceAccessor();
        var store = new ExecutionTraceStore(accessor);
        var tracer = new ExecutionTracer(store, accessor);
        var trace = store.Start("c", "how many leads?", "exec-prompt");
        accessor.CurrentExecutionId = trace.ExecutionId;

        tracer.SetOutboundAiPrompt(new
        {
            tools = new[] { "query_crm" },
            messages = new object[]
            {
                new { role = "system", content = "You are a read-only assistant." },
                new { role = "system", content = "<<<APPROVED_CRM_METADATA_CATALOG>>>{\"entities\":[]}<<<END_APPROVED_CRM_METADATA_CATALOG>>>" },
                new { role = "user", content = "how many leads?" }
            }
        });
        store.Complete("exec-prompt", ExecutionTraceStatus.Success);

        var snap = store.GetSnapshot("exec-prompt")!;
        Assert.NotNull(snap.OutboundAiPrompt);
        var text = TraceSanitizer.BuildSafeTraceText(snap);
        Assert.Contains("OutboundAiPrompt:", text);
        Assert.Contains("how many leads?", text);
        Assert.Contains("APPROVED_CRM_METADATA_CATALOG", text);
    }

    [Fact]
    public void Failed_Step_Is_Not_Marked_Success()
    {
        var trace = new ExecutionTrace("x");
        var step = trace.BeginStep(ExecutionSteps.DynamicsHttpRequest, "GET");
        trace.CompleteStep(step.StepId, ExecutionStepStatus.Failed, error: "HTTP 400");
        trace.Complete(ExecutionTraceStatus.Failed);
        var snap = trace.Snapshot();
        Assert.Equal(ExecutionTraceStatus.Failed, snap.Status);
        Assert.Contains(snap.Steps, s => s.Status == ExecutionStepStatus.Failed && s.Error != null);
        Assert.DoesNotContain(snap.Steps, s => s.Name == ExecutionSteps.DynamicsHttpRequest && s.Status == ExecutionStepStatus.Success);
    }

    [Fact]
    public void Inspector_Enabled_Only_In_Development_With_Flag()
    {
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Development };
        Assert.True(ExecutionInspectorEndpoints.IsInspectorEnabled(env, new CrmOptions { EnableExecutionInspector = true }));
        Assert.False(ExecutionInspectorEndpoints.IsInspectorEnabled(env, new CrmOptions { EnableExecutionInspector = false }));

        env.EnvironmentName = Environments.Production;
        Assert.False(ExecutionInspectorEndpoints.IsInspectorEnabled(env, new CrmOptions { EnableExecutionInspector = true }));
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
