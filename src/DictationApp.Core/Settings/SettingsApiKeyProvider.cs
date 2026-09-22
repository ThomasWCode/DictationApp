using DictationApp.Core.Abstractions;

namespace DictationApp.Core.Settings;

/// <summary>Resolves the API key from settings (DPAPI) with an environment-variable fallback for CLI use.</summary>
public sealed class SettingsApiKeyProvider(ISettingsStore settings, ISecretStore secrets) : IApiKeyProvider
{
    public const string EnvironmentVariable = "ASSEMBLYAI_API_KEY";

    public string? GetApiKey()
    {
        var protectedKey = settings.Current.ApiKeyProtected;
        if (!string.IsNullOrEmpty(protectedKey))
        {
            var key = secrets.Unprotect(protectedKey);
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key.Trim();
            }
        }

        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }
}
