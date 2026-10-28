using Microsoft.Extensions.AI;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Backward-compatible façade over <see cref="CrmQueryIntentHelper"/> for sales-order count routing.
/// </summary>
public static class SalesOrderIntentHelper
{
    public static bool LooksLikeSalesOrderQuestion(string message) =>
        string.Equals(CrmQueryIntentHelper.DetectEntityLogicalName(message), "salesorder", StringComparison.Ordinal);

    public static bool LooksLikeCountQuestion(string message) =>
        CrmQueryIntentHelper.LooksLikeCountQuestion(message);

    public static bool LooksLikeSalesOrderCount(string message) =>
        CrmQueryIntentHelper.LooksLikeSalesOrderCount(message);

    public static FunctionCallContent CorrectToolCall(string userMessage, FunctionCallContent call) =>
        CrmQueryIntentHelper.CorrectToolCall(userMessage, call);
}
