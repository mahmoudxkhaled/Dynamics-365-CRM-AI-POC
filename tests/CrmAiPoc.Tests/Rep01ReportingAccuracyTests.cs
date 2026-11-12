using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Crm.FetchXml;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Query;
using Microsoft.Extensions.AI;

namespace CrmAiPoc.Tests;

/// <summary>REP-01: aggregate columns, group identity, privacy-safe follow-up synopsis.</summary>
public sealed class Rep01ReportingAccuracyTests
{
    [Fact]
    public void InferColumnsFromRows_Preserves_Planned_And_Appends_Missing_Aliases()
    {
        var planned = new[]
        {
            new CrmTableColumn { Key = "revenue", Label = "Total Amount (Sum)", Kind = "money", LogicalName = "totalamount" }
        };
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?>
            {
                ["revenue"] = 10m,
                ["account_name"] = "Acme",
                ["account_id"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                ["omega_widgetid"] = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
            }
        };

        var cols = FetchXmlQueryExecutor.InferColumnsFromRows(planned, rows, primaryIdAttribute: "omega_widgetid");
        Assert.Equal(2, cols.Count);
        Assert.Equal("Total Amount (Sum)", cols[0].Label);
        Assert.Contains(cols, c => c.Key == "account_name");
        // Raw GUID-only keys and root primary id stay on the row but are not inferred as display columns.
        Assert.DoesNotContain(cols, c => c.Key == "account_id");
        Assert.DoesNotContain(cols, c => c.Key == "omega_widgetid");
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", rows[0]["account_id"]);
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", rows[0]["omega_widgetid"]);
    }

    [Fact]
    public void InferColumnsFromRows_Zero_Rows_Keeps_Planned_Schema()
    {
        var planned = new[]
        {
            new CrmTableColumn { Key = "revenue", Label = "Revenue", Kind = "money", LogicalName = "totalamount" },
            new CrmTableColumn { Key = "acct", Label = "Account", Kind = "string", LogicalName = "name" }
        };

        var cols = FetchXmlQueryExecutor.InferColumnsFromRows(planned, Array.Empty<IReadOnlyDictionary<string, object?>>());
        Assert.Equal(2, cols.Count);
        Assert.Equal("acct", cols[1].Key);
    }

    [Fact]
    public void MultiYear_Chunks_Cover_Span_Without_Overlap()
    {
        var tz = BusinessTimeZone.Resolve("Africa/Cairo");
        var min = new DateTimeOffset(2000, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var max = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var years = AggregateDatePartitioner.BuildYearPartitions(min, max, tz);
        Assert.True(years.Count > 12);

        var chunks = AggregateDatePartitioner.BuildMultiYearChunkPartitions(min, max, tz, yearsPerChunk: 3);
        Assert.True(chunks.Count < years.Count);
        Assert.Equal(years[0].StartInclusiveUtc, chunks[0].StartInclusiveUtc);
        Assert.Equal(years[^1].EndExclusiveUtc, chunks[^1].EndExclusiveUtc);
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i - 1].EndExclusiveUtc, chunks[i].StartInclusiveUtc);
        }
    }

    [Fact]
    public void PrivacySafe_Synopsis_Includes_Columns_Not_Records()
    {
        var data = new
        {
            entity = "omega_widget",
            operation = "aggregate",
            resultMode = "complete",
            joinedRowCount = 10,
            columns = new[]
            {
                new { key = "widget_name", label = "Widget Name", kind = "string", logicalName = "name" },
                new { key = "total_value", label = "Amount (Sum)", kind = "money", logicalName = "amount" }
            },
            appliedFilters = new[] { "Recovered AggregateQueryRecordLimit via partitions" },
            records = new[]
            {
                new Dictionary<string, object?> { ["widget_name"] = "SECRET", ["total_value"] = 999 }
            }
        };

        var synopsis = ChatService.TryFormatPrivacySafeResultSynopsis(data);
        Assert.NotNull(synopsis);
        Assert.Contains("omega_widget", synopsis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("widget_name", synopsis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Widget Name", synopsis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rowCount=10", synopsis, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET", synopsis, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("999", synopsis, StringComparison.Ordinal);
        Assert.True(AssistantHistoryPrivacyFilter.IsAllowed("answer", synopsis, structuredData: null));
    }

    [Fact]
    public void PrivacySafe_Synopsis_Allows_FollowUp_History_Without_Records()
    {
        var session = new ConversationSession("rep01-followup");
        Assert.True(session.TryAppendUserQuestion("Show top widgets by amount."));

        var synopsis = ChatService.TryFormatPrivacySafeResultSynopsis(new
        {
            entity = "omega_widget",
            operation = "aggregate",
            joinedRowCount = 5,
            columns = new[]
            {
                new { key = "name_alias", label = "Name", kind = "string", logicalName = "name" }
            }
        });
        Assert.NotNull(synopsis);
        Assert.True(session.TryAppendSafeAssistantTurn("answer", synopsis!));

        var selected = ChatService.SelectConversationWindow(
            session.GetTurnsSnapshot(),
            "Where is the name column?",
            maxMessages: 10,
            maxHistoryCharacters: 8000);

        var history = ChatService.BuildOutboundHistory(selected.Turns);
        Assert.Contains(history, m => m.Role == ChatRole.Assistant &&
                                      m.Text!.Contains("name_alias", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(history, m => m.Text != null && m.Text.Contains("\"records\"", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, selected.PreviousAssistantMessagesIncluded);
    }

    [Fact]
    public void Duplicate_Display_Names_Stay_Separate_With_Composite_Group_Keys()
    {
        var aggregates = new[]
        {
            new CrmAggregateSpec
            {
                Function = CrmAggregateFunction.Sum,
                Field = "amount",
                Alias = "total"
            }
        };
        var groups = new[] { "rec_id", "rec_name" };
        var merged = AggregateDatePartitioner.MergePartitionRows(
            [
                [
                    new Dictionary<string, object?>
                    {
                        ["rec_id"] = "id-1",
                        ["rec_name"] = "Same",
                        ["total"] = 10m
                    },
                    new Dictionary<string, object?>
                    {
                        ["rec_id"] = "id-2",
                        ["rec_name"] = "Same",
                        ["total"] = 20m
                    }
                ]
            ],
            aggregates,
            groups);

        Assert.Equal(2, merged.Count);
        Assert.Equal(10m, Convert.ToDecimal(merged.Single(r => Convert.ToString(r["rec_id"]) == "id-1")["total"]));
        Assert.Equal(20m, Convert.ToDecimal(merged.Single(r => Convert.ToString(r["rec_id"]) == "id-2")["total"]));
    }
}
