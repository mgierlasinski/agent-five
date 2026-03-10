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