using System.Text.Json;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Diagnostics;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Endpoints;

public static class ExecutionInspectorEndpoints
{
    public static RouteGroupBuilder MapExecutionInspectorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/diagnostics/executions").WithTags("ExecutionInspector");

        group.MapGet("/", (
            IExecutionTraceStore store,
            IHostEnvironment env,
            IOptions<CrmOptions> options,
            int? take) =>
        {
            if (!IsInspectorEnabled(env, options.Value))
            {
                return Results.Json(new { error = "Execution inspector is disabled." }, statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Ok(store.ListRecent(take ?? 20));
        })
        .WithName("ListExecutionTraces");

        group.MapGet("/{executionId}", (
            string executionId,
            IExecutionTraceStore store,
            IHostEnvironment env,
            IOptions<CrmOptions> options) =>
        {
            if (!IsInspectorEnabled(env, options.Value))
            {
                return Results.Json(new { error = "Execution inspector is disabled." }, statusCode: StatusCodes.Status404NotFound);
            }

            var snap = store.GetSnapshot(executionId);
            return snap is null ? Results.NotFound(new { error = "Execution not found." }) : Results.Ok(snap);
        })
        .WithName("GetExecutionTrace");

        group.MapGet("/{executionId}/safe-text", (
            string executionId,
            IExecutionTraceStore store,
            IHostEnvironment env,
            IOptions<CrmOptions> options) =>
        {
            if (!IsInspectorEnabled(env, options.Value))
            {
                return Results.Json(new { error = "Execution inspector is disabled." }, statusCode: StatusCodes.Status404NotFound);
            }

            var snap = store.GetSnapshot(executionId);
            if (snap is null)
            {
                return Results.NotFound(new { error = "Execution not found." });
            }

            return Results.Text(TraceSanitizer.BuildSafeTraceText(snap), "text/plain");
        })
        .WithName("GetExecutionTraceSafeText");

        group.MapGet("/{executionId}/events", async (
            string executionId,
            IExecutionTraceStore store,
            IHostEnvironment env,
            IOptions<CrmOptions> options,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            if (!IsInspectorEnabled(env, options.Value))
            {
                return Results.Json(new { error = "Execution inspector is disabled." }, statusCode: StatusCodes.Status404NotFound);
            }

            if (store.GetSnapshot(executionId) is null)
            {
                return Results.NotFound(new { error = "Execution not found." });
            }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Connection = "keep-alive";

            var lastJson = "";
            while (!cancellationToken.IsCancellationRequested)
            {
                var snap = store.GetSnapshot(executionId);
                if (snap is null)
                {
                    break;
                }

                var json = JsonSerializer.Serialize(snap);
                if (!string.Equals(json, lastJson, StringComparison.Ordinal))
                {
                    lastJson = json;
                    await http.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                    await http.Response.Body.FlushAsync(cancellationToken);
                }

                if (snap.Status is not ExecutionTraceStatus.Running)
                {
                    break;
                }

                await Task.Delay(350, cancellationToken);
            }

            return Results.Empty;
        })
        .WithName("StreamExecutionTrace");

        return group;
    }

    public static bool IsInspectorEnabled(IHostEnvironment env, CrmOptions options) =>
        env.IsDevelopment() && options.EnableExecutionInspector;
}
