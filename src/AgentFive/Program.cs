using AgentFive.Configuration;
using AgentFive.Tasks.FindHim;
using AgentFive.Tasks.People;
using AgentFive.Tasks.SendIt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var config = new ConfigurationBuilder()
    .AddJsonFile(ConfigHelper.GetPath("appsettings.json"), optional: true, reloadOnChange: true)
    .AddUserSecrets<Program>()
    .Build();

var hubSettings = config.GetSection(HubSettings.SectionName).Get<HubSettings>()!;
var openRouterSettings = config.GetSection(OpenRouterSettings.SectionName).Get<OpenRouterSettings>()!;

using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddConsole());
var logger = factory.CreateLogger<Program>();

var tasks = new Dictionary<string, Func<Task>>
{
    ["people"] = async () => await new PeopleTask(openRouterSettings, logger).RunAsync(),
    ["findhim"] = async () =>
    {
        if (!File.Exists(PeopleTask.PeopleTransport))
        {
            logger.LogInformation("Missing people transport input. Running PeopleTask first.");
            await new PeopleTask(openRouterSettings, logger).RunAsync();
        }
        await new FindHimTask(hubSettings, openRouterSettings, logger).RunAsync();
    },
    ["sendit"] = async () => await new SendItTask().RunAsync()
};

await tasks["sendit"].Invoke();
