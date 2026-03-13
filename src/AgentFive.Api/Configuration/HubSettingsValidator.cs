using AgentFive.Configuration;
using Microsoft.Extensions.Options;

namespace AgentFive.Api.Configuration;

public sealed class HubSettingsValidator : IValidateOptions<HubSettings>
{
    public ValidateOptionsResult Validate(string? name, HubSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.HubUrl))
        {
            failures.Add("HubUrl is required.");
        }

        if (string.IsNullOrWhiteSpace(settings.HubApiKey))
        {
            failures.Add("HubApiKey is required.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}