namespace CrmAiPoc.Api.Diagnostics;

/// <summary>
/// Async-local correlation id shared across AI, metadata, FetchXML, and CRM HTTP logs
/// for a single chat request.
/// </summary>
public static class DiagnosticsCorrelation
{
    private static readonly AsyncLocal<string?> CurrentId = new();

    public static string? Current => CurrentId.Value;

    public static string CurrentOrNone =>
        string.IsNullOrWhiteSpace(CurrentId.Value) ? "(none)" : CurrentId.Value!;

    public static IDisposable Begin(string? correlationId = null)
    {
        var previous = CurrentId.Value;
        CurrentId.Value = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString("N")[..12]
            : correlationId.Trim();
        return new Scope(previous);
    }

    public static string EnsureStarted()
    {
        if (string.IsNullOrWhiteSpace(CurrentId.Value))
        {
            CurrentId.Value = Guid.NewGuid().ToString("N")[..12];
        }

        return CurrentId.Value!;
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => CurrentId.Value = previous;
    }
}
