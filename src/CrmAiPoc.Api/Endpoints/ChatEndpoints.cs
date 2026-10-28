using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Models;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Endpoints;

public static class ChatEndpoints
{
    public static RouteGroupBuilder MapChatEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Chat");

        group.MapPost("/chat", async (
            ChatRequestDto request,
            ChatService chatService,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "Message is required." });
            }

            try
            {
                var response = await chatService.ChatAsync(request, cancellationToken);
                return Results.Ok(response);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(
                    new { error = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("Chat").LogError(ex, "Unexpected chat failure");
                return Results.Json(
                    new { error = ex.Message },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("Chat")
        .Produces<ChatResponseDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/health", (IOptions<AiOptions> aiOptions) => Results.Ok(new
        {
            status = "ok",
            scope = "CRM AI POC (read-only)",
            aiProvider = aiOptions.Value.Provider,
            availableAiProviders = new[] { "Gemini", "OpenAI", "Groq" },
            timestampUtc = DateTimeOffset.UtcNow
        }))
        .WithName("Health");

        return group;
    }
}
