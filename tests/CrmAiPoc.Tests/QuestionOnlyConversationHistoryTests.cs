using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Models;
using Microsoft.Extensions.AI;

namespace CrmAiPoc.Tests;

/// <summary>
/// Conversation history tests (CTX-01): privacy-safe user + assistant turns for Groq.
/// </summary>
public sealed class QuestionOnlyConversationHistoryTests
{
    [Fact]
    public void ScenarioA_First_Question_Is_System_Plus_Current_Only()
    {
        var history = ChatService.BuildOutboundHistory("Show the latest 5 accounts.");

        Assert.Equal(2, history.Count);
        Assert.Equal(ChatRole.System, history[0].Role);
        Assert.Equal(ChatRole.User, history[1].Role);
        Assert.Equal("Show the latest 5 accounts.", history[1].Text);
        Assert.DoesNotContain(history, m => m.Role == ChatRole.Assistant);
    }

    [Fact]
    public void ScenarioB_Follow_Up_Includes_Previous_And_Current_User_Questions()
    {
        var window = ChatService.SelectUserQuestionWindow(
            ["Show the latest 5 accounts."],
            "Show 10 instead.",
            maxUserQuestions: 10,
            maxUserHistoryCharacters: 8000);

        Assert.Equal(
            ["Show the latest 5 accounts.", "Show 10 instead."],
            window);

        var history = ChatService.BuildOutboundHistory(window);
        Assert.Equal(3, history.Count);
        Assert.All(history.Skip(1), m => Assert.Equal(ChatRole.User, m.Role));
        Assert.Equal("Show 10 instead.", history[^1].Text);
    }

    [Fact]
    public void ScenarioC_Clarification_Selection_Includes_Prior_Clarification_Assistant()
    {
        var q1 = "Show the latest 5 sales orders with customer and branch account names.";
        var clarification =
            "Which account relationship should I use?\n\nClarification options:\n" +
            "- Customer Account: Use the Customer Account relationship for these sales orders.\n" +
            "- Branch Account: Use the Branch Account relationship for these sales orders.";
        var q2 = "Use the Customer Account relationship for these sales orders.";

        var session = new ConversationSession("c-clarify");
        Assert.True(session.TryAppendUserQuestion(q1));
        Assert.True(session.TryAppendSafeAssistantTurn("clarification", clarification));

        var selected = ChatService.SelectConversationWindow(
            session.GetTurnsSnapshot(),
            q2,
            maxMessages: 10,
            maxHistoryCharacters: 8000);
        session.TryAppendUserQuestion(q2);

        var history = ChatService.BuildOutboundHistory(selected.Turns);
        Assert.Equal(ChatRole.System, history[0].Role);
        Assert.Equal(q1, history[1].Text);
        Assert.Equal(ChatRole.Assistant, history[2].Role);
        Assert.Contains("Customer Account", history[2].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Branch Account", history[2].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(q2, history[3].Text);
        Assert.Equal(1, selected.PreviousUserMessagesIncluded);
        Assert.Equal(1, selected.PreviousAssistantMessagesIncluded);
        Assert.True(selected.AssistantHistoryPrivacyFilterApplied);
        Assert.Equal(2, session.UserQuestionCount);
        Assert.Equal(3, session.TurnCount);
    }

    [Fact]
    public void ScenarioD_Multiple_Follow_Ups_Preserve_Chronological_Order()
    {
        var questions = new[]
        {
            "Show the latest 5 sales orders.",
            "Include customer account names.",
            "Show 10 instead of 5."
        };

        var session = new ConversationSession("c-multi");
        foreach (var q in questions[..^1])
        {
            Assert.True(session.TryAppendUserQuestion(q));
        }

        var window = ChatService.SelectUserQuestionWindow(
            session.GetUserQuestionsSnapshot(),
            questions[^1],
            maxUserQuestions: 10,
            maxUserHistoryCharacters: 8000);

        Assert.Equal(questions, window);
        var history = ChatService.BuildOutboundHistory(window);
        Assert.Equal(ChatRole.System, history[0].Role);
        Assert.Equal(3, history.Count(m => m.Role == ChatRole.User));
        Assert.Equal(questions, history.Skip(1).Select(m => m.Text).ToArray());
    }

    [Fact]
    public void ScenarioE_New_Conversation_Has_Empty_History()
    {
        var store = new ConversationStore();
        var a = store.GetOrCreate("conv-a");
        a.TryAppendUserQuestion("Show the latest 5 sales orders.");
        a.TryAppendSafeAssistantTurn("unsupported", ChatService.UnsupportedFeatureUserMessage);
        a.Touch();

        var b = store.GetOrCreate("conv-b");
        Assert.Empty(b.GetUserQuestionsSnapshot());
        Assert.Empty(b.GetTurnsSnapshot());

        var window = ChatService.SelectUserQuestionWindow(
            b.GetUserQuestionsSnapshot(),
            "Show opportunities.",
            maxUserQuestions: 10,
            maxUserHistoryCharacters: 8000);

        Assert.Equal(["Show opportunities."], window);
        Assert.DoesNotContain(window, q => q.Contains("sales orders", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            window,
            q => q.Contains(ChatService.UnsupportedFeatureUserMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void ScenarioF_Independent_Question_Still_Includes_Prior_User_Text_As_Context_Only()
    {
        var window = ChatService.SelectUserQuestionWindow(
            ["Show the latest 5 sales orders."],
            "Show the latest 5 opportunities.",
            maxUserQuestions: 10,
            maxUserHistoryCharacters: 8000);

        var history = ChatService.BuildOutboundHistory(window);
        Assert.Contains("independent", history[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Show the latest 5 opportunities.", history[^1].Text);
        Assert.Equal("Show the latest 5 sales orders.", history[1].Text);
    }

    [Fact]
    public void ScenarioG_Outbound_History_Excludes_Tool_And_Crm_Payloads()
    {
        var turns = new[]
        {
            new ConversationTurn { Role = ConversationTurnRole.User, Text = "Show the latest 5 accounts.", Kind = "user" },
            new ConversationTurn
            {
                Role = ConversationTurnRole.Assistant,
                Text = ChatService.FormatClarificationHistoryText(
                    "Which sort do you want?",
                    [
                        new ClarificationSuggestionDto
                        {
                            Label = "Newest",
                            Question = "Show the latest 5 accounts sorted by created on."
                        }
                    ]),
                Kind = "clarification"
            },
            new ConversationTurn
            {
                Role = ConversationTurnRole.User,
                Text = "Show the latest 5 accounts sorted by created on.",
                Kind = "user"
            }
        };

        var history = ChatService.BuildOutboundHistory(turns);

        var dialogue = history.Where(m => m.Role != ChatRole.System).ToList();
        Assert.DoesNotContain(
            dialogue,
            m => m.Contents.Any(c => c is FunctionCallContent or FunctionResultContent));
        Assert.DoesNotContain(
            dialogue,
            m => (m.Text ?? string.Empty).Contains("ORD-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            dialogue,
            m => (m.Text ?? string.Empty).Contains("<fetch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            dialogue,
            m => (m.Text ?? string.Empty).Contains("\"records\"", StringComparison.OrdinalIgnoreCase));

        var audit = OutboundAiPayloadAuditor.Audit(history);
        Assert.True(audit.SafeForLiveCrm);
        Assert.False(audit.ContainsFunctionResults);
    }

    [Fact]
    public void ScenarioH_History_Limits_Keep_Current_Question()
    {
        var prior = Enumerable.Range(1, 15)
            .Select(i => $"Show accounts page {i} with a moderately long clarifying phrase.")
            .ToArray();

        var current = "Show the latest 5 opportunities now.";
        var window = ChatService.SelectUserQuestionWindow(
            prior,
            current,
            maxUserQuestions: 10,
            maxUserHistoryCharacters: 8000);

        Assert.Equal(10, window.Count);
        Assert.Equal(current, window[^1]);
        Assert.DoesNotContain(prior[0], window);
        Assert.Contains(prior[^1], window);

        var tight = ChatService.SelectUserQuestionWindow(
            ["AAAA long prior context that should be dropped when the budget is tiny"],
            current,
            maxUserQuestions: 10,
            maxUserHistoryCharacters: current.Length);
        Assert.Equal([current], tight);
        Assert.Equal(current, tight[0]);
    }

    [Fact]
    public void ScenarioI_Arabic_And_Mixed_Questions_Preserved_Untranslated()
    {
        var arabic = "هات آخر 5 أوامر بيع";
        var mixed = "هات آخر 5 Sales Orders مع أسماء العملاء";

        var session = new ConversationSession("c-ar");
        Assert.True(session.TryAppendUserQuestion(arabic));

        var window = ChatService.SelectUserQuestionWindow(
            session.GetUserQuestionsSnapshot(),
            mixed,
            maxUserQuestions: 10,
            maxUserHistoryCharacters: 8000);

        Assert.Equal([arabic, mixed], window);
        var history = ChatService.BuildOutboundHistory(window);
        Assert.Equal(arabic, history[1].Text);
        Assert.Equal(mixed, history[2].Text);
    }

    [Fact]
    public void Session_Skips_Consecutive_Duplicate_Questions()
    {
        var session = new ConversationSession("c-dup");
        Assert.True(session.TryAppendUserQuestion("Show the latest 5 accounts."));
        Assert.False(session.TryAppendUserQuestion("Show the latest 5 accounts."));
        Assert.True(session.TryAppendUserQuestion("Show 10 instead."));
        Assert.Equal(2, session.UserQuestionCount);
    }

    [Fact]
    public void Rejected_Empty_Question_Is_Not_Appended()
    {
        var session = new ConversationSession("c-empty");
        Assert.False(session.TryAppendUserQuestion("   "));
        Assert.Equal(0, session.UserQuestionCount);
    }

    [Fact]
    public void System_Prompt_Explains_Privacy_Safe_Conversation_Context()
    {
        var history = ChatService.BuildOutboundHistory("Show accounts.");
        Assert.Contains("most recent user message is the current request", history[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("independent questions must not inherit", history[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clarification", history[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivacyFilter_Rejects_FetchXml_And_Record_Payloads()
    {
        Assert.False(AssistantHistoryPrivacyFilter.IsAllowed(
            "answer",
            "<fetch top=\"5\"><entity name=\"account\"/></fetch>",
            structuredData: null));
        Assert.False(AssistantHistoryPrivacyFilter.IsAllowed(
            "answer",
            """{"records":[{"name":"Acme"}]}""",
            structuredData: null));
        Assert.False(AssistantHistoryPrivacyFilter.IsAllowed(
            "clarification",
            "Which field?",
            structuredData: new { records = Array.Empty<object>() }));
        Assert.True(AssistantHistoryPrivacyFilter.IsAllowed(
            "unsupported",
            ChatService.UnsupportedFeatureUserMessage,
            structuredData: null));
    }

    [Fact]
    public void Unsupported_Assistant_Turn_Is_Included_Safely()
    {
        var turns = new[]
        {
            new ConversationTurn { Role = ConversationTurnRole.User, Text = "Convert amounts to EUR.", Kind = "user" },
            new ConversationTurn
            {
                Role = ConversationTurnRole.Assistant,
                Text = ChatService.UnsupportedFeatureUserMessage,
                Kind = "unsupported"
            },
            new ConversationTurn { Role = ConversationTurnRole.User, Text = "Show latest accounts instead.", Kind = "user" }
        };

        var history = ChatService.BuildOutboundHistory(turns);
        Assert.Equal(ChatRole.Assistant, history[2].Role);
        Assert.Equal(ChatService.UnsupportedFeatureUserMessage, history[2].Text);
        Assert.DoesNotContain(history, m => (m.Text ?? string.Empty).Contains("Cross-currency", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Truncation_Drops_Oldest_Including_Assistant_Turns()
    {
        var prior = new List<ConversationTurn>
        {
            new() { Role = ConversationTurnRole.User, Text = "Old question one about accounts.", Kind = "user" },
            new()
            {
                Role = ConversationTurnRole.Assistant,
                Text = "Which status filter should I apply?\n\nClarification options:\n- Active: Show only active accounts.\n- All: Show all accounts.",
                Kind = "clarification"
            },
            new() { Role = ConversationTurnRole.User, Text = "Show only active accounts.", Kind = "user" }
        };

        var selected = ChatService.SelectConversationWindow(
            prior,
            "Now show contacts.",
            maxMessages: 2,
            maxHistoryCharacters: 8000);

        Assert.True(selected.HistoryTruncated);
        Assert.Equal(2, selected.Turns.Count);
        Assert.Equal("Now show contacts.", selected.Turns[^1].Text);
        Assert.DoesNotContain(selected.Turns, t => t.Text.Contains("Old question one", StringComparison.Ordinal));
    }

    [Fact]
    public void Session_Idle_Cleanup_Does_Not_Leak_Across_Conversations()
    {
        var store = new ConversationStore();
        var first = store.GetOrCreate("ttl-a");
        first.TryAppendUserQuestion("Show accounts.");
        first.TryAppendSafeAssistantTurn("unsupported", ChatService.UnsupportedFeatureUserMessage);

        var second = store.GetOrCreate("ttl-b");
        Assert.Empty(second.GetTurnsSnapshot());
        Assert.NotEqual(first.ConversationId, second.ConversationId);
    }
}
