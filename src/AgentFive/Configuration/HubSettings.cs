namespace AgentFive.Configuration;

public class HubSettings
{
    public const string SectionName = "Hub";

    public string HubUrl { get; set; } = null!;
    public string HubApiKey { get; set; } = null!;
}