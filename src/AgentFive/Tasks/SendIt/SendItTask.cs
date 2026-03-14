using System.Text.Json;
using AgentFive.Configuration;
using AgentFive.Services.OpenRouter;
using AgentFive.Tasks.SendIt.Models;
using AgentFive.Tasks.SendIt.Tools;
using Microsoft.Extensions.Logging;

namespace AgentFive.Tasks.SendIt;

public class SendItTask
{
    private const string DeclarationOutput = "sendit_declaration.txt";
    private const int MaxAgentIterations = 12;

    private readonly ILogger _logger;
    private readonly OpenRouterService _openRouter;
    private readonly SpkClient _spkClient;
    private readonly DeclarationService _declarationService;
    private readonly DeclarationRequest _shipment;

    public SendItTask(HubSettings hubSettings, OpenRouterSettings openRouterSettings, ILogger logger)
    {
        _logger = logger;
        _openRouter = new OpenRouterService(openRouterSettings, logger);
        _spkClient = new SpkClient(hubSettings, logger);
        _declarationService = new DeclarationService();
        _shipment = new DeclarationRequest(
            "450202122",
            "Gdańsk",
            "Żarnowiec",
            2800m,
            0m,
            "kasety z paliwem do reaktora",
            string.Empty);
    }

    public async Task RunAsync()
    {
        try
        {
            var toolHandler = new SpkToolHandler(_openRouter, _declarationService, _spkClient, _shipment, _logger);
            var result = await _openRouter.RunToolConversationAsync<SendItAgentResult>(
                BuildSystemPrompt(),
                BuildUserPrompt(),
                SpkToolProvider.CreateTools(),
                toolHandler.HandleToolCallAsync,
                OpenRouterModels.Gpt5Mini,
                BuildResponseSchema(),
                0.0,
                MaxAgentIterations).ConfigureAwait(false);

            if (result == null)
            {
                throw new InvalidOperationException("SendIt agent did not return a final result.");
            }

            await File.WriteAllTextAsync(DeclarationOutput, result.Declaration).ConfigureAwait(false);
            _logger.LogInformation(
                "SendIt completed. Submitted={Submitted}, Category={Category}, RouteCode={RouteCode}, AmountDuePp={AmountDuePp}, AdditionalWagons={AdditionalWagons}",
                result.Submitted,
                result.Category,
                result.RouteCode,
                result.AmountDuePp,
                result.AdditionalWagons);
            _logger.LogInformation("SendIt summary: {Summary}", result.Summary);
            _logger.LogInformation("SendIt verification response: {VerificationResponse}", result.VerificationResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendIt task failed");
        }
        finally
        {
            _spkClient.Dispose();
            _openRouter.Dispose();
        }
    }

    private string BuildSystemPrompt()
    {
        return string.Join(Environment.NewLine,
            "You are an autonomous logistics agent solving the task 'sendit'.",
            "You must work through tool calls, read the documentation thoroughly, and submit the completed declaration through the submit_declaration tool.",
            "Rules:",
            "1. Start from index.md and read every referenced document that is relevant to route selection, declaration format, fees, restrictions, and abbreviations.",
            "2. If a referenced document is an image, fetch it and analyze it with the vision tool before deciding.",
            "3. Do not invent route codes, categories, or fees. Derive them from documentation or tool outputs.",
            "4. The declaration must match the template formatting exactly, including separators, line order, and blank special notes.",
            "5. Before submission, ensure the shipment complies with the route restrictions for Żarnowiec and with the 0 PP budget constraint.",
            "6. Use find_route_code before filling the TRASA field.",
            "7. When you believe the declaration is correct, call submit_declaration with the full declaration text.",
            "8. If submit_declaration returns an error or rejection, fix the declaration and retry within the remaining tool budget.",
            "9. Return only the final JSON object matching the schema. Do not include markdown.");
    }

    private string BuildUserPrompt()
    {
        var today = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
        var payload = new
        {
            task = "sendit",
            today,
            documentationUrl = "https://hub.ag3nts.org/dane/doc/index.md",
            shipment = new
            {
                senderId = _shipment.SenderId,
                origin = _shipment.Origin,
                destination = _shipment.Destination,
                weightKg = _shipment.WeightKg,
                budgetPp = _shipment.BudgetPp,
                contentDescription = _shipment.ContentDescription,
                specialNotes = _shipment.SpecialNotes
            },
            requirements = new[]
            {
                "The answer sent to the hub must be POST /verify with task sendit and answer.declaration equal to the full declaration text.",
                "Do not add any special notes.",
                "Use a category and route that are allowed for transport to Żarnowiec.",
                "The declaration must stay within 0 PP total cost."
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
    }

    private static JsonSchemaObject BuildResponseSchema()
    {
        return new JsonSchemaObject(
            "sendit_result",
            true,
            new
            {
                type = "object",
                properties = new
                {
                    declaration = new { type = "string" },
                    category = new { type = "string" },
                    routeCode = new { type = "string" },
                    amountDuePp = new { type = "number" },
                    additionalWagons = new { type = "integer" },
                    submitted = new { type = "boolean" },
                    verificationResponse = new { type = "string" },
                    summary = new { type = "string" }
                },
                required = new[] { "declaration", "category", "routeCode", "amountDuePp", "additionalWagons", "submitted", "verificationResponse", "summary" },
                additionalProperties = false
            });
    }
}
