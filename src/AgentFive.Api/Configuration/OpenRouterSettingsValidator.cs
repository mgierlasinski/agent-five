using AgentFive.Configuration;
using Microsoft.Extensions.Options;

namespace AgentFive.Api.Configuration;

public sealed class OpenRouterSettingsValidator : IValidateOptions<OpenRouterSettings>
{
    public ValidateOptionsResult Validate(string? name, OpenRouterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.OpenRouterUrl))
        {
            failures.Add("OpenRouterUrl is required.");
        }

        if (string.IsNullOrWhiteSpace(settings.OpenRouterApiKey))
        {
            failures.Add("OpenRouterApiKey is required.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}