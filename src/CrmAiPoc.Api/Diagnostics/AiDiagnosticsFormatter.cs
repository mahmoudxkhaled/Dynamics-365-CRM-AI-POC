using System.Text;
using System.Text.Json;
using CrmAiPoc.Api.Ai;
using Microsoft.Extensions.AI;

namespace CrmAiPoc.Api.Diagnostics;

/// <summary>Formats MEAI messages / responses for diagnostic sections (no secrets).</summary>
public static class AiDiagnosticsFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string FormatMessages(
        IReadOnlyList<ChatMessage> messages,
        PipelineDiagnostics diagnostics,
        bool includeCatalogFullText)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            sb.AppendLine($"--- Message[{i}] Role={m.Role} ---");
            if (string.IsNullOrWhiteSpace(m.Text))
            {
                foreach (var content in m.Contents)
                {
                    sb.AppendLine($"ContentType: {content.GetType().Name}");
                }

                continue;
            }

            if (OutboundAiPayloadAuditor.IsApprovedMetadataCatalogBlock(m.Text))
            {
                if (includeCatalogFullText)
                {
                    sb.AppendLine(m.Text);
                }
                else
                {
                    sb.AppendLine(
                        $"(APPROVED_CRM_METADATA_CATALOG block, chars={m.Text.Length}; full text logged under MetadataCatalogSentToAi)");
                }
            }
            else if (m.Role == ChatRole.User)
            {
                sb.AppendLine(diagnostics.SanitizeUserQuestion(m.Text));
            }
            else
            {
                sb.AppendLine(m.Text);
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatChatResponse(ChatResponse response, PipelineDiagnostics diagnostics)
    {
        var payload = new
        {
            response.ModelId,
            FinishReason = response.FinishReason?.ToString(),
            Usage = response.Usage is null
                ? null
                : new
                {
                    response.Usage.InputTokenCount,
                    response.Usage.OutputTokenCount,
                    response.Usage.TotalTokenCount
                },
            Messages = response.Messages.Select(m => new
            {
                Role = m.Role.ToString(),
                Text = m.Text,
                FunctionCalls = m.Contents.OfType<FunctionCallContent>().Select(fc => new
                {
                    fc.CallId,
                    fc.Name,
                    Arguments = fc.Arguments
                }),
                TextContents = m.Contents.OfType<TextContent>().Select(t => t.Text)
            }),
            ResponseText = response.Text
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return diagnostics.AllowSensitivePayloads ? json : diagnostics.RedactText(json);
    }

    public static string FormatToolDefinitions(IEnumerable<AITool> tools)
    {
        var list = new List<object>();
        foreach (var tool in tools)
        {
            if (tool is AIFunction fn)
            {
                list.Add(new
                {
                    name = fn.Name,
                    description = fn.Description,
                    jsonSchema = fn.JsonSchema
                });
            }
            else
            {
                list.Add(new { name = tool.Name, type = tool.GetType().Name });
            }
        }

        return JsonSerializer.Serialize(list, JsonOptions);
    }

    public static string FormatToolArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return "(null)";
        }

        try
        {
            return JsonSerializer.Serialize(arguments, JsonOptions);
        }
        catch
        {
            return string.Join(", ", arguments.Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }
}
