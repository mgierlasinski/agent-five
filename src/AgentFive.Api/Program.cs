using AgentFive.Api.Configuration;
using AgentFive.Api.Contracts;
using AgentFive.Api.Services;
using AgentFive.Configuration;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services
	.AddOptions<HubSettings>()
	.Bind(builder.Configuration.GetSection(HubSettings.SectionName))
	.ValidateOnStart();

builder.Services
	.AddOptions<OpenRouterSettings>()
	.Bind(builder.Configuration.GetSection(OpenRouterSettings.SectionName))
	.ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<HubSettings>, HubSettingsValidator>();
builder.Services.AddSingleton<IValidateOptions<OpenRouterSettings>, OpenRouterSettingsValidator>();

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
	logger.LogInformation("Proxy request for session {SessionId}: {Message}", request.SessionID, request.Msg);

	var response = await assistantService.ProcessAsync(request, cancellationToken).ConfigureAwait(false);
    logger.LogInformation("Proxy response for session {SessionId}: {Response}", request.SessionID, response.Msg);

	return Results.Ok(response);
});

app.Run();
