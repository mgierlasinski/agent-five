using System;

namespace AgentFive.Tasks.People;

public record Person(
	string FirstName,
	string LastName,
	string Gender,
	DateTime DateOfBirth,
	string City,
	string Country,
	string Description
);

public record TaggedPerson(
	string name, 
	string surname, 
	string gender, 
	int born, 
	string city, 
	List<string> tags);