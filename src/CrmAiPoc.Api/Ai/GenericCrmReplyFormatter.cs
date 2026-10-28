using System.Globalization;
using System.Text;
using CrmAiPoc.Api.Crm.Generic;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Entity-independent local formatter for generic CRM query results (Arabic/English).
/// </summary>
public static class GenericCrmReplyFormatter
{
    public static string Format(GenericCrmQueryResult result, string languageHint, Guid? crmUserId = null)
    {
        var ar = languageHint.Equals("ar", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        if (result.Operation == CrmQueryOperation.Count)
        {
            if (result.Count is null)
            {
                return ar
                    ? (result.Message ?? $"تعذر حساب عدد سجلات {result.Entity}.")
                    : (result.Message ?? $"Could not count {result.Entity} records.");
            }

            if (result.CountMayBeCapped || result.CountLimitExceededAnnotation)
            {
                sb.AppendLine(ar
                    ? $"عدد سجلات {result.Entity} المطابقة هو على الأقل {result.Count} — ليس عدداً دقيقاً (Dynamics totalrecordcountlimitexceeded / سقف العد)."
                    : $"Matching {result.Entity} count is at least {result.Count} — not an exact total (Dynamics totalrecordcountlimitexceeded / count ceiling).");
            }
            else
            {
                sb.AppendLine(ar
                    ? $"عدد سجلات {result.Entity} المطابقة: {result.Count}."
                    : $"Matching {result.Entity} count: {result.Count}.");
            }
        }
        else if (result.Items.Count == 0)
        {
            sb.AppendLine(ar
                ? $"لا توجد سجلات {result.Entity} مطابقة ضمن صلاحياتك الحالية."
                : $"No {result.Entity} records matched for the current authorized CRM identity.");
        }
        else
        {
            sb.AppendLine(ar
                ? $"تم جلب {result.Items.Count} سجل/سجلات من {result.Entity} ({result.ProviderName}):"
                : $"Retrieved {result.Items.Count} {result.Entity} record(s) ({result.ProviderName}):");

            var i = 1;
            foreach (var row in result.Items)
            {
                sb.AppendLine($"{i}. {FormatRow(row, result)}");
                i++;
            }
        }

        if (result.AppliedFilterSummary.Count > 0)
        {
            sb.AppendLine(ar ? "الفلاتر المطبّقة:" : "Applied filters:");
            foreach (var filter in result.AppliedFilterSummary)
            {
                sb.AppendLine($"- {filter}");
            }
        }

        if (!string.IsNullOrWhiteSpace(result.SortSummary) && result.Operation == CrmQueryOperation.List)
        {
            sb.AppendLine(ar ? $"الترتيب: {result.SortSummary}" : $"Sort: {result.SortSummary}");
        }

        if (crmUserId is Guid id)
        {
            sb.AppendLine(ar
                ? $"هوية CRM المستخدمة للقراءة: {id}"
                : $"CRM read identity (WhoAmI UserId): {id}");
        }

        sb.AppendLine(ar
            ? "ملاحظة: تم تجهيز الرد محلياً ولم تُرسل سجلات CRM إلى نموذج الذكاء الاصطناعي."
            : "Note: This reply was generated locally; CRM records were not sent to the AI model.");

        return sb.ToString().TrimEnd();
    }

    private static string FormatRow(IReadOnlyDictionary<string, object?> row, GenericCrmQueryResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.PrimaryNameAttribute) &&
            TryGet(row, result.PrimaryNameAttribute!, out var name) &&
            !string.IsNullOrWhiteSpace(name))
        {
            parts.Add(name!);
        }

        foreach (var key in new[] { "ordernumber", "accountnumber", "emailaddress1", "telephone1", "createdon", "totalamount", "jobtitle" })
        {
            if (TryGet(row, key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                parts.Add(FormatValue(key, value!));
            }
        }

        foreach (var key in new[] { "statecode_label", "statuscode_label", "statecode", "statuscode" })
        {
            if (TryGet(row, key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value!);
                break;
            }
        }

        return parts.Count > 0 ? string.Join(" | ", parts) : "(record)";
    }

    private static bool TryGet(IReadOnlyDictionary<string, object?> row, string key, out string? value)
    {
        value = null;
        if (!row.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        value = raw switch
        {
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            decimal d => d.ToString("N2", CultureInfo.InvariantCulture),
            double db => db.ToString("N2", CultureInfo.InvariantCulture),
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture)
        };
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string FormatValue(string key, string value) =>
        key.Equals("totalamount", StringComparison.OrdinalIgnoreCase) ? value : value;
}
