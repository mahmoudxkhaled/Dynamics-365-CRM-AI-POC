using System.Globalization;
using System.Text;
using System.Text.Json;
using CrmAiPoc.Api.Crm.Models;
using CrmAiPoc.Api.Crm.Query;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Builds user-facing chat replies in .NET (Arabic/English) from CRM tool results.
/// Never sends these payloads to an external LLM.
/// </summary>
public static class LocalCrmReplyFormatter
{
    public static string FormatSalesOrders(
        IReadOnlyList<CrmSalesOrder> orders,
        int requestedLimit,
        string languageHint,
        string providerName,
        Guid? crmUserId)
    {
        return FormatQueryResult(new SalesOrderQueryExecutionResult
        {
            Operation = SalesOrderQueryOperation.List,
            RelativeRequestUri = "(get_sales_orders)",
            AppliedFilterSummary = ["latest orders"],
            SortSummary = "createdon desc",
            Limit = requestedLimit,
            Items = orders,
            ProviderName = providerName
        }, languageHint, crmUserId);
    }

    public static string FormatQueryResult(
        SalesOrderQueryExecutionResult result,
        string languageHint,
        Guid? crmUserId)
    {
        var ar = languageHint.Equals("ar", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        if (result.Operation == SalesOrderQueryOperation.Count)
        {
            if (result.Count is null)
            {
                return ar
                    ? (result.Message ?? "تعذر حساب عدد أوامر البيع.")
                    : (result.Message ?? "Could not count sales orders.");
            }

            if (result.CountMayBeCapped)
            {
                sb.AppendLine(ar
                    ? $"عدد أوامر البيع المطابقة من CRM هو على الأقل {result.Count} (قد يكون العدد الحقيقي أكبر بسبب حد العد في Dynamics)."
                    : $"Matching sales order count from CRM is at least {result.Count} (true total may be higher due to the Dynamics @odata.count ceiling).");
            }
            else
            {
                sb.AppendLine(ar
                    ? $"عدد أوامر البيع المطابقة: {result.Count}."
                    : $"Matching sales order count: {result.Count}.");
            }
        }
        else if (result.Items.Count == 0)
        {
            sb.AppendLine(ar
                ? "لا توجد أوامر بيع مطابقة للفلاتر المطلوبة ضمن صلاحياتك الحالية."
                : "No sales orders matched the requested filters for the current authorized CRM identity.");
        }
        else
        {
            sb.AppendLine(ar
                ? $"تم جلب {result.Items.Count} أمر/أوامر بيع من Dynamics 365 ({result.ProviderName}):"
                : $"Retrieved {result.Items.Count} sales order(s) from Dynamics 365 ({result.ProviderName}):");

            var i = 1;
            foreach (var order in result.Items)
            {
                var name = string.IsNullOrWhiteSpace(order.Name) ? "(no name)" : order.Name.Trim();
                var number = string.IsNullOrWhiteSpace(order.OrderNumber) ? "-" : order.OrderNumber;
                var created = order.CreatedOn?.ToString("yyyy-MM-dd") ?? "-";
                var amount = order.TotalAmount?.ToString("N2", CultureInfo.InvariantCulture) ?? "-";
                var state = order.StateLabel ?? order.StateCode?.ToString(CultureInfo.InvariantCulture) ?? "-";
                var status = order.StatusLabel ?? order.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "-";
                sb.AppendLine($"{i}. {name} | {number} | {created} | {amount} | {state}/{status}");
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

        if (!string.IsNullOrWhiteSpace(result.SortSummary) && result.Operation == SalesOrderQueryOperation.List)
        {
            sb.AppendLine(ar
                ? $"الترتيب: {result.SortSummary}"
                : $"Sort: {result.SortSummary}");
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

    public static string FormatToolJson(string toolName, string json, string languageHint)
    {
        var ar = languageHint.Equals("ar", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.True)
            {
                var message = doc.RootElement.TryGetProperty("message", out var m)
                    ? m.GetString()
                    : null;
                return message ?? (ar ? "فشل تنفيذ الأداة." : "Tool execution failed.");
            }

            if (string.Equals(toolName, "count_accounts", StringComparison.OrdinalIgnoreCase) &&
                doc.RootElement.TryGetProperty("count", out var countEl) &&
                countEl.TryGetInt32(out var accountCount))
            {
                return ar
                    ? $"عدد الحسابات/العملاء المتاح لك: {accountCount}."
                    : $"Account/customer count available to you: {accountCount}.";
            }

            return ar
                ? $"تم تنفيذ الأداة `{toolName}` بنجاح. التفاصيل معروضة في البيانات المنظمة."
                : $"Tool `{toolName}` completed successfully. See structured data in the response.";
        }
        catch
        {
            return ar ? "تعذر تفسير نتيجة الأداة." : "Could not interpret the tool result.";
        }
    }

    public static string FormatRefusalOrText(string? modelText, string languageHint)
    {
        if (!string.IsNullOrWhiteSpace(modelText))
        {
            return modelText.Trim();
        }

        return languageHint.Equals("ar", StringComparison.OrdinalIgnoreCase)
            ? "يمكنني المساعدة فقط في استعلامات CRM للقراءة على كيان salesorder والحسابات/جهات الاتصال المعتمدة."
            : "I can only help with approved read-only CRM queries (salesorder plus allowlisted account/contact tools).";
    }
}
