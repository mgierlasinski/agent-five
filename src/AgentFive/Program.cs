using AgentFive.Configuration;
using AgentFive.Tasks.FindHim;
using AgentFive.Tasks.People;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var config = new ConfigurationBuilder()
    .AddJsonFile(ConfigHelper.GetPath("appsettings.json"), optional: true, reloadOnChange: true)
    .AddUserSecrets<Program>()
    .Build();

var settings = config.Get<AppSettings>()!;

using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddConsole());
var logger = factory.CreateLogger<Program>();

if (!File.Exists(ConfigHelper.GetPath(PeopleFiles.PeopleTransport)))
{
    logger.LogInformation("Missing people transport input. Running PeopleTask first.");
    await new PeopleTask(settings, logger).RunAsync();
}

await new FindHimTask(settings, logger).RunAsync();
