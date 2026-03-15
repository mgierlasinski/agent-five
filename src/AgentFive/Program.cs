using AgentFive.Configuration;
using AgentFive.Tasks.FindHim;
using AgentFive.Tasks.People;
using AgentFive.Tasks.Railway;
using AgentFive.Tasks.SendIt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var config = AppStartup.BuildConfiguration();
var logger = AppStartup.CreateLogger();

var hubSettings = config.GetSection(HubSettings.SectionName).Get<HubSettings>()!;
var openRouterSettings = config.GetSection(OpenRouterSettings.SectionName).Get<OpenRouterSettings>()!;

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
    ["sendit"] = async () => await new SendItTask(hubSettings, openRouterSettings, logger).RunAsync(),
    ["railway"] = async () => await new RailwayTask(hubSettings, openRouterSettings, logger).RunAsync()
};

logger.LogInformation("Starting AgentFive. Available tasks: {Tasks}", string.Join(", ", tasks.Keys));
await tasks["railway"].Invoke();
