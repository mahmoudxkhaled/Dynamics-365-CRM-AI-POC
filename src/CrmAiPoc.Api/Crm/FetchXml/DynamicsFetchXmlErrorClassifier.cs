using CrmAiPoc.Api.Crm.Generic;

namespace CrmAiPoc.Api.Crm.FetchXml;

/// <summary>
/// Classifies Dynamics FetchXML execution failures for bounded AI self-correction.
/// Only clearly semantic/syntax FetchXML mistakes are recoverable — never auth, transport, or empty success.
/// </summary>
public static class DynamicsFetchXmlErrorClassifier
{
    public sealed class Classification
    {
        public bool IsRecoverable { get; init; }
        public string ErrorCode { get; init; } = "DYNAMICS_FETCHXML_ERROR";
        public string SanitizedFeedback { get; init; } = "Dynamics rejected the FetchXML query.";
        public string UserMessage { get; init; } = "The CRM query could not be executed.";
    }

    public static Classification Classify(
        int httpStatus,
        DynamicsCrmErrorParser.ParsedError error)
    {
        var code = error.Code;
        var message = error.Message ?? string.Empty;

        if (httpStatus is 401 or 403)
        {
            return NonRetryable(
                "AUTHORIZATION_DENIED",
                "CRM authorization failed for this query.",
                "CRM authorization failed for this query.");
        }

        if (httpStatus is >= 500 and <= 599)
        {
            return NonRetryable(
                "CRM_SERVICE_FAILURE",
                "The CRM service failed while executing the query.",
                "The CRM service is temporarily unavailable. Please try again later.");
        }

        if (httpStatus == 408)
        {
            return NonRetryable(
                "CRM_TIMEOUT",
                "The CRM request timed out.",
                "The CRM request timed out. Please try again.");
        }

        // AggregateQueryRecordLimit is an infrastructure capacity error — not an invalid field.
        // Deterministic partition recovery owns this path; do not send it to Groq for re-planning.
        if (error.IsAggregateQueryRecordLimit ||
            DynamicsCrmErrorParser.IsAggregateLimit(code, message))
        {
            return NonRetryable(
                "AGGREGATE_RECORD_LIMIT_EXCEEDED",
                Truncate(
                    "Dynamics AggregateQueryRecordLimit (0x8004E023) exceeded. " +
                    "Backend partition recovery should handle this; do not rewrite the business query for this code."),
                Truncate(
                    "CRM AggregateQueryRecordLimit (0x8004E023) exceeded for this aggregate. " +
                    TrimMessage(message)));
        }

        // Do not treat every HTTP 400 as recoverable.
        if (httpStatus != 400 && httpStatus != 404)
        {
            return NonRetryable(
                code ?? "DYNAMICS_FETCHXML_ERROR",
                Truncate($"Dynamics rejected the FetchXML query (HTTP {httpStatus}, Code={code})."),
                $"Dynamics Web API FetchXML GET failed. Status={httpStatus}, Code={code}.");
        }

        if (LooksLikeRecoverableSemanticError(code, message))
        {
            var feedback = BuildRecoverableFeedback(code, message);
            return new Classification
            {
                IsRecoverable = true,
                ErrorCode = MapRecoverableCode(code, message),
                SanitizedFeedback = feedback,
                UserMessage = Truncate(
                    $"Dynamics rejected the FetchXML query (Code={code}). {TrimMessage(message)}")
            };
        }

        return NonRetryable(
            code ?? "DYNAMICS_FETCHXML_ERROR",
            Truncate($"Dynamics rejected the FetchXML query (HTTP {httpStatus}, Code={code})."),
            Truncate(
                $"Dynamics Web API FetchXML GET failed. Status={httpStatus}, Code={code}."));
    }

    private static Classification NonRetryable(string code, string feedback, string userMessage) =>
        new()
        {
            IsRecoverable = false,
            ErrorCode = code,
            SanitizedFeedback = feedback,
            UserMessage = userMessage
        };

    private static bool LooksLikeRecoverableSemanticError(string? code, string message)
    {
        // Prefer precise FetchXML-shape signals. Avoid bare "Invalid Argument" and entity names
        // that contain substrings like "order" (e.g. salesorder).
        if (HasFetchXmlSemanticSignal(message))
        {
            return true;
        }

        // Aggregate-order mistakes commonly surface as 80041131 even with sparse messages.
        if (string.Equals(code, "80041131", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 80040203 is generic InvalidArgument — never recover from the code alone.
        return false;
    }

    private static bool HasFetchXmlSemanticSignal(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        if (message.Equals("Invalid Argument", StringComparison.OrdinalIgnoreCase) ||
            message.Equals("An error has occurred.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ContainsAny(
            message,
            "order by",
            "orderby",
            "<order",
            "sort by",
            "alias",
            "aggregate",
            "groupby",
            "group by",
            "attribute",
            "column",
            "fetchxml",
            "invalid fetch",
            "does not exist",
            "is not valid",
            "not a valid",
            "unknown attribute",
            "specified order");
    }

    private static string MapRecoverableCode(string? dynamicsCode, string message)
    {
        if (ContainsAny(message, "order by", "orderby", "<order", "sort by", "specified order", "alias"))
        {
            return "INVALID_SORT";
        }

        // Never map AggregateQueryRecordLimit here — handled above as AGGREGATE_RECORD_LIMIT_EXCEEDED.
        if (ContainsAny(message, "aggregate") &&
            !DynamicsCrmErrorParser.IsAggregateLimit(dynamicsCode, message))
        {
            return "INVALID_AGGREGATE_FIELD";
        }

        if (ContainsAny(message, "groupby", "group by"))
        {
            return "INVALID_GROUP_BY_FIELD";
        }

        if (ContainsAny(message, "attribute", "column", "does not exist", "not a valid"))
        {
            return "INVALID_FIELD";
        }

        return string.IsNullOrWhiteSpace(dynamicsCode)
            ? "INVALID_FETCHXML"
            : $"DYNAMICS_{dynamicsCode}";
    }

    private static string BuildRecoverableFeedback(string? code, string message)
    {
        var detail = TrimMessage(message);
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = $"Dynamics rejected the FetchXML (Code={code}).";
        }

        return Truncate(
            detail +
            " Revise the FetchXML: for aggregate queries sort with <order alias=\"...\"/> using a declared " +
            "aggregate/groupby alias; ensure attributes, aliases, and relationships are valid. " +
            "Do not claim CRM data was already retrieved.");
    }

    private static string TrimMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var trimmed = message.Trim();
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            trimmed = trimmed[..newline].Trim();
        }

        return trimmed;
    }

    private static string Truncate(string value, int max = 400)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return value[..max] + "…";
    }

    private static bool ContainsAny(string message, params string[] fragments) =>
        fragments.Any(f => message.Contains(f, StringComparison.OrdinalIgnoreCase));
}
