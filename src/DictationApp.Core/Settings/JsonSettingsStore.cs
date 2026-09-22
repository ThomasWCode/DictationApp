using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DictationApp.Core.Settings;

/// <summary>
/// JSON file store with atomic writes (write to a temp file, then replace). A corrupt file is moved aside
/// and defaults are used, so a bad edit never bricks the app.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(string filePath, ILogger<JsonSettingsStore> logger)
    {
        FilePath = filePath;
        _logger = logger;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public string FilePath { get; }

    public event Action<AppSettings>? Changed;

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = FilePath + ".tmp";
            var json = JsonSerializer.Serialize(settings, Options);
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, FilePath, overwrite: true);
            Current = settings;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(settings);
    }

    public async Task UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        var copy = Current.Clone();
        mutate(copy);
        await SaveAsync(copy, ct).ConfigureAwait(false);
    }

    private AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (settings is null)
            {
                throw new JsonException("settings.json deserialised to null");
            }

            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            var backup = FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            _logger.LogError(ex, "settings.json is unreadable; moving it to {Backup} and using defaults", backup);
            try
            {
                File.Move(FilePath, backup, overwrite: true);
            }
            catch (IOException moveEx)
            {
                _logger.LogWarning(moveEx, "Could not move corrupt settings file");
            }

            return new AppSettings();
        }
    }
}
