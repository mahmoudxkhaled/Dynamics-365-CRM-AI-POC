using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace CrmAiPoc.Api.Diagnostics;

public enum ExecutionStepStatus
{
    Pending,
    Running,
    Success,
    Failed,
    Blocked
}

public enum ExecutionTraceStatus
{
    Running,
    Success,
    Failed,
    Blocked
}

/// <summary>Canonical step names for the inspector timeline (only emitted when actually executed).</summary>
public static class ExecutionSteps
{
    public const string HttpChatRequestReceived = "HTTP Chat Request Received";
    public const string GroqApiRequestPrepared = "Groq API Request Prepared";
    public const string GroqApiResponseReceived = "Groq API Response Received";
    public const string AiToolSelected = "AI Tool Selected";
    public const string StructuredQueryPlanParsed = "Structured Query Plan Parsed";
    public const string BackendValidation = "Backend Validation";
    public const string MetadataResolution = "Metadata / Relationship Resolution";
    public const string QueryConstruction = "Generic OData Query Construction";
    public const string DynamicsHttpRequest = "Dynamics 365 HTTP Request";
    public const string DynamicsHttpResponse = "Dynamics 365 HTTP Response";
    public const string LocalResponseFormatting = "Local Response Formatting";
    public const string PrivacyVerification = "Privacy Verification";
    public const string MetadataCatalogPrepared = "Approved Metadata Catalog Prepared";
    public const string FinalChatResponse = "Final Chat Response";
}

public sealed class ExecutionStepEvent
{
    public required string StepId { get; init; }
    public required string Name { get; init; }
    public ExecutionStepStatus Status { get; set; } = ExecutionStepStatus.Pending;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public long? DurationMs { get; set; }
    public string? Summary { get; set; }
    public object? Details { get; set; }
    public string? Error { get; set; }
}

public sealed class ExecutionTraceSnapshot
{
    public required string ExecutionId { get; init; }
    public string? ConversationId { get; init; }
    public string? MessagePreview { get; init; }
    public ExecutionTraceStatus Status { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public long? TotalDurationMs { get; init; }
    public string? AiProvider { get; init; }
    public string? AiModel { get; init; }
    public string? SelectedTool { get; init; }
    public object? ToolArguments { get; init; }
    public object? CrmQuery { get; init; }
    public object? Metrics { get; init; }
    public object? OutboundAiPrompt { get; init; }
    public bool PrivacySafe { get; init; }
    public bool OutboundContainedCrmRecords { get; init; }
    public IReadOnlyList<ExecutionStepEvent> Steps { get; init; } = Array.Empty<ExecutionStepEvent>();
}

public sealed class ExecutionTrace
{
    private readonly object _gate = new();
    private readonly List<ExecutionStepEvent> _steps = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _runningStarts = new(StringComparer.Ordinal);

    public string ExecutionId { get; }
    public string? ConversationId { get; set; }
    public string? MessagePreview { get; set; }
    public ExecutionTraceStatus Status { get; private set; } = ExecutionTraceStatus.Running;
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string? AiProvider { get; set; }
    public string? AiModel { get; set; }
    public string? SelectedTool { get; set; }
    public object? ToolArguments { get; set; }
    public object? CrmQuery { get; set; }
    public object? OutboundAiPrompt { get; set; }
    public Dictionary<string, long?> Metrics { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool PrivacySafe { get; set; } = true;
    public bool OutboundContainedCrmRecords { get; set; }

    public ExecutionTrace(string executionId)
    {
        ExecutionId = executionId;
    }

    public ExecutionStepEvent BeginStep(string name, string? summary = null, object? details = null)
    {
        lock (_gate)
        {
            var step = new ExecutionStepEvent
            {
                StepId = Guid.NewGuid().ToString("N")[..12],
                Name = name,
                Status = ExecutionStepStatus.Running,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Summary = summary,
                Details = details
            };
            _steps.Add(step);
            _runningStarts[step.StepId] = step.StartedAtUtc;
            return step;
        }
    }

    public void CompleteStep(
        string stepId,
        ExecutionStepStatus status,
        string? summary = null,
        object? details = null,
        string? error = null)
    {
        lock (_gate)
        {
            var step = _steps.FirstOrDefault(s => s.StepId == stepId);
            if (step is null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            step.CompletedAtUtc = now;
            step.DurationMs = (long)(now - step.StartedAtUtc).TotalMilliseconds;
            step.Status = status;
            if (summary is not null)
            {
                step.Summary = summary;
            }

            if (details is not null)
            {
                step.Details = details;
            }

            if (error is not null)
            {
                step.Error = error;
            }

            _runningStarts.TryRemove(stepId, out _);
        }
    }

    public void AddInstantStep(
        string name,
        ExecutionStepStatus status,
        string? summary = null,
        object? details = null,
        string? error = null,
        long? durationMs = null)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _steps.Add(new ExecutionStepEvent
            {
                StepId = Guid.NewGuid().ToString("N")[..12],
                Name = name,
                Status = status,
                StartedAtUtc = durationMs is null ? now : now - TimeSpan.FromMilliseconds(durationMs.Value),
                CompletedAtUtc = now,
                DurationMs = durationMs ?? 0,
                Summary = summary,
                Details = details,
                Error = error
            });
        }
    }

    public void Complete(ExecutionTraceStatus status)
    {
        lock (_gate)
        {
            Status = status;
            CompletedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public ExecutionTraceSnapshot Snapshot()
    {
        lock (_gate)
        {
            var completed = CompletedAtUtc;
            return new ExecutionTraceSnapshot
            {
                ExecutionId = ExecutionId,
                ConversationId = ConversationId,
                MessagePreview = MessagePreview,
                Status = Status,
                StartedAtUtc = StartedAtUtc,
                CompletedAtUtc = completed,
                TotalDurationMs = completed is null
                    ? (long)(DateTimeOffset.UtcNow - StartedAtUtc).TotalMilliseconds
                    : (long)(completed.Value - StartedAtUtc).TotalMilliseconds,
                AiProvider = AiProvider,
                AiModel = AiModel,
                SelectedTool = SelectedTool,
                ToolArguments = ToolArguments,
                CrmQuery = CrmQuery,
                Metrics = Metrics.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                OutboundAiPrompt = OutboundAiPrompt,
                PrivacySafe = PrivacySafe,
                OutboundContainedCrmRecords = OutboundContainedCrmRecords,
                Steps = _steps.Select(CloneStep).ToArray()
            };
        }
    }

    private static ExecutionStepEvent CloneStep(ExecutionStepEvent s) =>
        new()
        {
            StepId = s.StepId,
            Name = s.Name,
            Status = s.Status,
            StartedAtUtc = s.StartedAtUtc,
            CompletedAtUtc = s.CompletedAtUtc,
            DurationMs = s.DurationMs,
            Summary = s.Summary,
            Details = s.Details,
            Error = s.Error
        };
}
