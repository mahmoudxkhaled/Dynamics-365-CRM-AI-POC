using System.Collections.Concurrent;

namespace CrmAiPoc.Api.Diagnostics;

public interface IExecutionTraceStore
{
    ExecutionTrace Start(string? conversationId, string? messagePreview, string? preferredExecutionId = null);
    bool TryGet(string executionId, out ExecutionTrace trace);
    ExecutionTraceSnapshot? GetSnapshot(string executionId);
    IReadOnlyList<ExecutionTraceSnapshot> ListRecent(int take = 20);
    void Complete(string executionId, ExecutionTraceStatus status);
}

/// <summary>
/// Bounded in-memory execution trace store for local Development inspector.
/// Concurrent chat requests are isolated by execution id.
/// </summary>
public sealed class ExecutionTraceStore : IExecutionTraceStore
{
    private readonly ConcurrentDictionary<string, ExecutionTrace> _traces = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly int _capacity;
    private readonly ExecutionTraceAccessor _accessor;

    public ExecutionTraceStore(ExecutionTraceAccessor accessor, int capacity = 100)
    {
        _accessor = accessor;
        _capacity = Math.Clamp(capacity, 10, 500);
    }

    public ExecutionTrace Start(string? conversationId, string? messagePreview, string? preferredExecutionId = null)
    {
        var id = string.IsNullOrWhiteSpace(preferredExecutionId)
            ? Guid.NewGuid().ToString("N")
            : preferredExecutionId.Trim();
        if (id.Length > 64)
        {
            id = id[..64];
        }

        var trace = new ExecutionTrace(id)
        {
            ConversationId = conversationId,
            MessagePreview = TraceSanitizer.PreviewMessage(messagePreview)
        };
        _traces[id] = trace;
        _order.Enqueue(id);
        _accessor.CurrentExecutionId = id;
        Trim();
        return trace;
    }

    public bool TryGet(string executionId, out ExecutionTrace trace) =>
        _traces.TryGetValue(executionId, out trace!);

    public ExecutionTraceSnapshot? GetSnapshot(string executionId) =>
        _traces.TryGetValue(executionId, out var t) ? t.Snapshot() : null;

    public IReadOnlyList<ExecutionTraceSnapshot> ListRecent(int take = 20)
    {
        take = Math.Clamp(take, 1, 100);
        return _order
            .Reverse()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => _traces.ContainsKey(id))
            .Take(take)
            .Select(id => _traces[id].Snapshot())
            .ToArray();
    }

    public void Complete(string executionId, ExecutionTraceStatus status)
    {
        if (_traces.TryGetValue(executionId, out var t))
        {
            t.Complete(status);
        }
    }

    private void Trim()
    {
        while (_traces.Count > _capacity && _order.TryDequeue(out var oldId))
        {
            _traces.TryRemove(oldId, out _);
        }
    }
}

/// <summary>AsyncLocal binding so nested services can append to the active chat execution.</summary>
public sealed class ExecutionTraceAccessor
{
    private static readonly AsyncLocal<string?> Current = new();

    public string? CurrentExecutionId
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}

public interface IExecutionTracer
{
    ExecutionTrace? Current { get; }
    ExecutionStepEvent? Begin(string name, string? summary = null, object? details = null);
    void Complete(string stepId, ExecutionStepStatus status, string? summary = null, object? details = null, string? error = null);
    void Instant(string name, ExecutionStepStatus status, string? summary = null, object? details = null, string? error = null, long? durationMs = null);
    void SetMetric(string name, long? valueMs);
    void SetTool(string? tool, object? arguments);
    void SetCrmQuery(object? query);
    void SetAi(string? provider, string? model);
    void SetPrivacy(bool privacySafe, bool outboundContainedCrm);
}

public sealed class ExecutionTracer : IExecutionTracer
{
    private readonly IExecutionTraceStore _store;
    private readonly ExecutionTraceAccessor _accessor;

    public ExecutionTracer(IExecutionTraceStore store, ExecutionTraceAccessor accessor)
    {
        _store = store;
        _accessor = accessor;
    }

    public ExecutionTrace? Current =>
        !string.IsNullOrWhiteSpace(_accessor.CurrentExecutionId) &&
        _store.TryGet(_accessor.CurrentExecutionId!, out var t)
            ? t
            : null;

    public ExecutionStepEvent? Begin(string name, string? summary = null, object? details = null) =>
        Current?.BeginStep(name, summary, TraceSanitizer.SanitizeObject(details));

    public void Complete(
        string stepId,
        ExecutionStepStatus status,
        string? summary = null,
        object? details = null,
        string? error = null) =>
        Current?.CompleteStep(stepId, status, summary, TraceSanitizer.SanitizeObject(details), error is null ? null : TraceSanitizer.RedactInline(error));

    public void Instant(
        string name,
        ExecutionStepStatus status,
        string? summary = null,
        object? details = null,
        string? error = null,
        long? durationMs = null) =>
        Current?.AddInstantStep(
            name,
            status,
            summary,
            TraceSanitizer.SanitizeObject(details),
            error is null ? null : TraceSanitizer.RedactInline(error),
            durationMs);

    public void SetMetric(string name, long? valueMs)
    {
        if (Current is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Current.Metrics[name] = valueMs;
    }

    public void SetTool(string? tool, object? arguments)
    {
        if (Current is null)
        {
            return;
        }

        Current.SelectedTool = tool;
        Current.ToolArguments = arguments is IDictionary<string, object?> dict
            ? TraceSanitizer.SanitizeToolArguments(dict)
            : TraceSanitizer.SanitizeObject(arguments);
    }

    public void SetCrmQuery(object? query)
    {
        if (Current is not null)
        {
            Current.CrmQuery = TraceSanitizer.SanitizeObject(query);
        }
    }

    public void SetAi(string? provider, string? model)
    {
        if (Current is null)
        {
            return;
        }

        Current.AiProvider = provider;
        Current.AiModel = model;
    }

    public void SetPrivacy(bool privacySafe, bool outboundContainedCrm)
    {
        if (Current is null)
        {
            return;
        }

        Current.PrivacySafe = privacySafe;
        Current.OutboundContainedCrmRecords = outboundContainedCrm;
    }
}

/// <summary>No-op tracer for unit tests that do not assert on execution inspection.</summary>
public sealed class NullExecutionTracer : IExecutionTracer
{
    public static NullExecutionTracer Instance { get; } = new();
    public ExecutionTrace? Current => null;
    public ExecutionStepEvent? Begin(string name, string? summary = null, object? details = null) => null;
    public void Complete(string stepId, ExecutionStepStatus status, string? summary = null, object? details = null, string? error = null) { }
    public void Instant(string name, ExecutionStepStatus status, string? summary = null, object? details = null, string? error = null, long? durationMs = null) { }
    public void SetMetric(string name, long? valueMs) { }
    public void SetTool(string? tool, object? arguments) { }
    public void SetCrmQuery(object? query) { }
    public void SetAi(string? provider, string? model) { }
    public void SetPrivacy(bool privacySafe, bool outboundContainedCrm) { }
}
