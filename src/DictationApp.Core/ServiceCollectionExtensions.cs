using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;
using DictationApp.Core.History;
using DictationApp.Core.Insertion;
using DictationApp.Core.Session;
using DictationApp.Core.Settings;
using DictationApp.Core.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DictationApp.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything platform-independent. The host must also register the platform adapters:
    /// IHotkeyService, IAudioCaptureFactory, IAudioSinkFactory, IForegroundContextProvider, ITextInserter,
    /// IClipboard, INotifier, ISecretStore.
    /// </summary>
    public static IServiceCollection AddDictationCore(this IServiceCollection services, AppPaths paths)
    {
        services.TryAddSingleton(paths);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISettingsStore>(sp => new JsonSettingsStore(paths.SettingsFile, sp.GetRequiredService<ILogger<JsonSettingsStore>>()));
        services.TryAddSingleton<IApiKeyProvider, SettingsApiKeyProvider>();
        services.TryAddSingleton<IHistoryRepository>(sp => new SqliteHistoryRepository(paths.HistoryDb, sp.GetRequiredService<ILogger<SqliteHistoryRepository>>()));
        services.TryAddSingleton<IStreamingTranscriberFactory, StreamingTranscriberFactory>();
        services.TryAddSingleton<PassthroughPostProcessor>();
        services.TryAddSingleton<LlmPostProcessor>();
        services.TryAddSingleton<ITextPostProcessor, PostProcessorRouter>();
        services.TryAddSingleton<DictationStatusHub>();
        services.TryAddSingleton<InsertionTextFormatter>();
        services.TryAddSingleton<DictationOrchestrator>();
        services.AddHostedService(sp => sp.GetRequiredService<DictationOrchestrator>());
        services.AddHttpClient(LlmPostProcessor.HttpClientName, client =>
        {
            client.BaseAddress = LlmPostProcessor.DefaultBaseAddress;
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        return services;
    }

    private sealed class StreamingTranscriberFactory(IApiKeyProvider keys, ILoggerFactory loggerFactory) : IStreamingTranscriberFactory
    {
        public IStreamingTranscriber Create() => new AssemblyAiStreamingTranscriber(keys, loggerFactory.CreateLogger<AssemblyAiStreamingTranscriber>());
    }
}
