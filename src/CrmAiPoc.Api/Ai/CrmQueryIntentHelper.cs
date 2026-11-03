using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Corrects query_crm tool args from the current user message (entity, count, sort, limit).
/// </summary>
public static partial class CrmQueryIntentHelper
{
    public static string? DetectEntityLogicalName(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        if (SalesOrderEntityRegex().IsMatch(message))
        {
            return "salesorder";
        }

        if (LeadEntityRegex().IsMatch(message))
        {
            return "lead";
        }

        if (ContactEntityRegex().IsMatch(message))
        {
            return "contact";
        }

        if (AccountEntityRegex().IsMatch(message))
        {
            return "account";
        }

        return null;
    }

    public static bool LooksLikeCountQuestion(string message) =>
        !string.IsNullOrWhiteSpace(message) && CountIntentRegex().IsMatch(message);

    public static FunctionCallContent CorrectToolCall(string userMessage, FunctionCallContent call)
    {
        if (!string.Equals(call.Name, "query_crm", StringComparison.OrdinalIgnoreCase))
        {
            return call;
        }

        var detectedEntity = DetectEntityLogicalName(userMessage);
        var args = call.Arguments is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(call.Arguments, StringComparer.OrdinalIgnoreCase);

        if (detectedEntity is not null)
        {
            var selectedEntity = GetArgString(args, "entity");
            if (!string.Equals(selectedEntity, detectedEntity, StringComparison.OrdinalIgnoreCase))
            {
                args["entity"] = detectedEntity;
                args.Remove("relatedFromEntity");
                args.Remove("relatedFromId");
            }
        }

        if (LooksLikeCountQuestion(userMessage))
        {
            args["operation"] = "count";
        }
        else if (string.IsNullOrWhiteSpace(GetArgString(args, "operation")))
        {
            args["operation"] = "list";
        }

        ApplyLimitHint(userMessage, args);
        ApplyValueSortHint(userMessage, args);

        if (string.Equals(GetArgString(args, "entity"), "salesorder", StringComparison.OrdinalIgnoreCase) &&
            ActiveIntentRegex().IsMatch(userMessage) &&
            string.IsNullOrWhiteSpace(GetArgString(args, "filtersJson")) &&
            string.IsNullOrWhiteSpace(GetArgString(args, "filters")))
        {
            args["filtersJson"] = """[{"field":"statecode","operator":"eq","value":"Active"}]""";
        }

        return new FunctionCallContent(call.CallId, "query_crm", args);
    }

    private static void ApplyLimitHint(string message, IDictionary<string, object?> args)
    {
        var match = LimitHintRegex().Match(message);
        if (match.Success)
        {
            var raw = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) &&
                limit is >= 1 and <= 50)
            {
                args["limit"] = limit;
                return;
            }
        }

        args.TryAdd("limit", 10);
    }

    private static void ApplyValueSortHint(string message, IDictionary<string, object?> args)
    {
        if (!string.Equals(GetArgString(args, "entity"), "salesorder", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (HighestValueIntentRegex().IsMatch(message))
        {
            args["sortField"] = "totalamount";
            args["sortDirection"] = "desc";
            args.Remove("sortJson");
            return;
        }

        if (LowestValueIntentRegex().IsMatch(message))
        {
            args["sortField"] = "totalamount";
            args["sortDirection"] = "asc";
            args.Remove("sortJson");
        }
    }

    private static string? GetArgString(IDictionary<string, object?>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return Convert.ToString(raw, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(
        @"sales\s*orders?|salesorder|أوامر\s*البيع|اوامر\s*البيع|امر\s*بيع|أمر\s*بيع|\bso\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SalesOrderEntityRegex();

    [GeneratedRegex(
        @"\bleads?\b|العملاء\s*المحتمل|عميل\s*محتمل|ليد",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadEntityRegex();

    [GeneratedRegex(
        @"\bcontacts?\b|جهات\s*اتصال|كونتاكت",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContactEntityRegex();

    [GeneratedRegex(
        @"\baccounts?\b|\bcustomers?\b|حسابات|عملاء|عميل|حساب",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccountEntityRegex();

    [GeneratedRegex(
        @"\bcount\b|\bhow\s+many\b|عدد|كام\s*عدد|كم\s*عدد|كم\s*أمر|كم\s*اوردر|إحصاء|احصاء",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CountIntentRegex();

    [GeneratedRegex(
        @"\bactive\b|نشط|فعّال|فعال",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveIntentRegex();

    [GeneratedRegex(
        @"(?:آخر|اخر|last|latest|top)\s+(\d{1,2})|\b(\d{1,2})\s+(?:accounts?|contacts?|leads?|sales\s*orders?|أوامر)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LimitHintRegex();

    [GeneratedRegex(
        @"highest[\s\-]*value|highest[\s\-]*amount|most\s+expensive|top[\s\-]*value|by\s+(?:highest\s+)?(?:value|amount)|أعلى\s*قيمة|الاعلى\s*قيمة|الأعلى\s*قيمة|أعلى\s*مبلغ",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HighestValueIntentRegex();

    [GeneratedRegex(
        @"lowest[\s\-]*value|lowest[\s\-]*amount|least\s+expensive|cheapest|by\s+lowest\s+(?:value|amount)|أقل\s*قيمة|الاقل\s*قيمة|الأقل\s*قيمة|أقل\s*مبلغ",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LowestValueIntentRegex();
}
