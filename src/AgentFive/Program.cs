using AgentFive.Configuration;
using AgentFive.Tasks.People;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .AddJsonFile(ConfigHelper.GetPath("appsettings.json"), optional: true, reloadOnChange: true)
    .AddUserSecrets<Program>()
    .Build();

var settings = config.Get<AppSettings>()!;
var peopleTask = new PeopleTask(settings);
var people = peopleTask.GetMalesAged20To40FromGrudziadz("Tasks/People/people.csv");

// Example: tag people using OpenRouter (if configured)
try
{
    var opts = new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    var tagged = await peopleTask.TagPeopleWithOpenRouterAsync(people);
    File.WriteAllText("tagged_people.json", System.Text.Json.JsonSerializer.Serialize(tagged, opts));

    var transportOnly = tagged.Where(x => x.tags.Contains("transport")).ToList();
    File.WriteAllText("transport_people.json", System.Text.Json.JsonSerializer.Serialize(transportOnly, opts));

    Console.WriteLine("Tagged people:");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(transportOnly, opts));
}
catch (Exception ex)
{
    Console.WriteLine($"OpenRouter tagging skipped or failed: {ex.Message}");
}