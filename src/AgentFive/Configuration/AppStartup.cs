using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace AgentFive.Configuration;

public static class AppStartup
{
    public static IConfiguration BuildConfiguration()
    {
        var basePath = AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();
        var config = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddUserSecrets<Program>()
            .Build();

        return config;
    }

    public static ILogger CreateLogger()
    {
        using var log = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File("log-.txt", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddSerilog(log));
        return factory.CreateLogger("AppLogger");
    }
}
