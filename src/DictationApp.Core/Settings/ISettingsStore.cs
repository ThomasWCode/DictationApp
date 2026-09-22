namespace DictationApp.Core.Settings;

public interface ISettingsStore
{
    /// <summary>The live settings object. Treat as read-only; use <see cref="UpdateAsync"/> to change it.</summary>
    AppSettings Current { get; }

    string FilePath { get; }

    event Action<AppSettings>? Changed;

    Task SaveAsync(AppSettings settings, CancellationToken ct = default);

    Task UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default);
}
