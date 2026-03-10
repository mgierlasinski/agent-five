using AgentFive.Configuration;
using AgentFive.Tasks.People;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();
    
var settings = config.Get<AppSettings>()!;
var peopleTask = new PeopleTask();
var people = peopleTask.GetMalesAged20To40FromGrudziadz("Tasks/People/people.csv");
peopleTask.DebugPrintPeople(people);