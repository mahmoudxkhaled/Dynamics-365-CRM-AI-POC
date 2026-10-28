namespace CrmAiPoc.Api.Crm.Models;

public sealed class CrmQueryResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public int TotalMatched { get; init; }
    public string? Message { get; init; }

    public static CrmQueryResult<T> Empty(string? message = null) =>
        new() { Items = Array.Empty<T>(), TotalMatched = 0, Message = message };
}
