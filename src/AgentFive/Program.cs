using AgentFive.Configuration;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();
    
var settings = config.Get<AppSettings>()!;
DebugKeys(settings);

static void DebugKeys(AppSettings settings)
{
    Console.WriteLine($"Hub URL: {settings.HubUrl}");
    Console.WriteLine($"Hub API Key: {settings.HubApiKey}");
    Console.WriteLine($"OpenRouter API Key: {settings.OpenRouterApiKey}");
}
