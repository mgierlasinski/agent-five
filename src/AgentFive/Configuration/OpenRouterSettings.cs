namespace AgentFive.Configuration;

public class OpenRouterSettings
{
    public const string SectionName = "OpenRouter";

    public string OpenRouterUrl { get; set; } = null!;
    public string OpenRouterApiKey { get; set; } = null!;
    public string OpenRouterModel { get; set; } = null!;
}