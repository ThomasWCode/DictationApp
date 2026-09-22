using DictationApp.Core.Abstractions;

namespace DictationApp.Core.Settings;

/// <summary>Resolves API keys from settings (DPAPI) with environment-variable fallbacks for CLI use.</summary>
public sealed class SettingsApiKeyProvider(ISettingsStore settings, ISecretStore secrets) : IApiKeyProvider
{
    public const string EnvironmentVariable = "ASSEMBLYAI_API_KEY";
    public const string LlmEnvironmentVariable = "GROQ_API_KEY";

    public string? GetApiKey() => Resolve(settings.Current.ApiKeyProtected, EnvironmentVariable);

    public string? GetLlmApiKey() => Resolve(settings.Current.GroqApiKeyProtected, LlmEnvironmentVariable);

    private string? Resolve(string? protectedValue, string environmentVariable)
    {
        if (!string.IsNullOrEmpty(protectedValue))
        {
            var key = secrets.Unprotect(protectedValue);
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key.Trim();
            }
        }

        var env = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }
}
