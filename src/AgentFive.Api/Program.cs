using AgentFive.Api.Configuration;
using AgentFive.Api.Contracts;
using AgentFive.Api.Services;
using AgentFive.Configuration;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
	.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
	.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
	.AddEnvironmentVariables()
	.AddUserSecrets<Program>(optional: true);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.ConfigureHttpJsonOptions(options =>
{
	options.SerializerOptions.PropertyNamingPolicy = null;
});

builder.Services
	.AddOptions<AppSettings>()
	.Bind(builder.Configuration)
	.ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<PackageHubClient>();
builder.Services.AddSingleton<ProxyAssistantService>();

var app = builder.Build();

app.UseExceptionHandler(exceptionApp =>
{
	exceptionApp.Run(async context =>
	{
		context.Response.StatusCode = StatusCodes.Status200OK;
		context.Response.ContentType = "application/json";

		await context.Response.WriteAsJsonAsync(new ProxyResponse(
			"Wybacz, system mi sie na chwile zawiesil, mozesz powtorzyc?"));
	});
});

app.MapGet("/", () => Results.Ok(new { status = "ok" }));

app.MapPost("/proxy", async (
	ProxyRequest request,
	ProxyAssistantService assistantService,
	ILoggerFactory loggerFactory,
	CancellationToken cancellationToken) =>
{
	var validationError = ProxyRequestValidator.Validate(request);
	if (validationError is not null)
	{
		return Results.BadRequest(new { error = validationError });
	}

	var logger = loggerFactory.CreateLogger("ProxyEndpoint");
	logger.LogInformation("Received proxy request for session {SessionId}: {Message}", request.SessionID, request.Msg);

	var response = await assistantService.ProcessAsync(request, cancellationToken).ConfigureAwait(false);
	return Results.Ok(response);
});

app.Run();
