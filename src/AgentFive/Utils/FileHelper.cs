namespace AgentFive.Utils;

public static class FileHelper
{
    public static string BasePath => AppContext.BaseDirectory;

    public static Task WriteArtifactAsync(string taskName, string fileName, string content)
    {
        var artifactsDir = Path.Combine(BasePath, "Artifacts", taskName);
        Directory.CreateDirectory(artifactsDir);
        var filePath = Path.Combine(artifactsDir, fileName);

        return File.WriteAllTextAsync(filePath, content);
    }
}
