using AgentFive.Configuration;
using AgentFive.Tasks.People;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .AddJsonFile(ConfigHelper.GetPath("appsettings.json"), optional: true, reloadOnChange: true)
    .AddUserSecrets<Program>()
    .Build();

var settings = config.Get<AppSettings>()!;

await new PeopleTask(settings).RunAsync();
