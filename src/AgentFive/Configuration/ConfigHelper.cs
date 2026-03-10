namespace AgentFive.Configuration;

public static class ConfigHelper
{
    public static string GetPath(string configFile)
    {
        // Ensure we look for appsettings.json next to the running assembly (build output)
        var basePath = AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();
        return Path.Combine(basePath, configFile);
    }
}
