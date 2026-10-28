using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Validation;

public sealed class CrmArgumentValidator
{
    private readonly CrmOptions _options;

    public CrmArgumentValidator(IOptions<CrmOptions> options)
    {
        _options = options.Value;
    }

    public ValidationResult ValidateSearchName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValidationResult.Fail("Parameter 'name' is required and cannot be empty.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > 200)
        {
            return ValidationResult.Fail("Parameter 'name' exceeds the maximum length of 200 characters.");
        }

        // Reject obvious injection / script payloads; queries are still parameterized / server-built.
        if (ContainsDangerousPayload(trimmed))
        {
            return ValidationResult.Fail("Parameter 'name' contains disallowed content.");
        }

        return ValidationResult.Ok(trimmed);
    }

    public ValidationResult ValidateAccountId(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return ValidationResult.Fail("Parameter 'accountId' is required.");
        }

        if (!Guid.TryParse(accountId.Trim(), out var id) || id == Guid.Empty)
        {
            return ValidationResult.Fail("Parameter 'accountId' must be a valid non-empty GUID.");
        }

        return ValidationResult.Ok(id.ToString());
    }

    public int ClampLimit(int? limit)
    {
        var value = limit is null or <= 0 ? _options.DefaultPageSize : limit.Value;
        return Math.Clamp(value, 1, _options.MaxPageSize);
    }

    private static bool ContainsDangerousPayload(string value)
    {
        ReadOnlySpan<string> blocked =
        [
            "<script",
            "javascript:",
            "--",
            ";--",
            "xp_",
            "fetchxml",
            "<fetch"
        ];

        foreach (var token in blocked)
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class ValidationResult
{
    public required bool IsValid { get; init; }
    public string? Error { get; init; }
    public string? NormalizedValue { get; init; }

    public static ValidationResult Ok(string? normalized = null) =>
        new() { IsValid = true, NormalizedValue = normalized };

    public static ValidationResult Fail(string error) =>
        new() { IsValid = false, Error = error };
}
