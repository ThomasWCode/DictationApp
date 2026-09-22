namespace DictationApp.Core.Settings;

/// <summary>Well-known locations under <c>%LOCALAPPDATA%\DictationApp</c>.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
        SettingsFile = Path.Combine(root, "settings.json");
        HistoryDb = Path.Combine(root, "history.db");
        AudioDirectory = Path.Combine(root, "audio");
        LogDirectory = Path.Combine(root, "logs");
    }

    public static AppPaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DictationApp"));

    public string Root { get; }

    public string SettingsFile { get; }

    public string HistoryDb { get; }

    public string AudioDirectory { get; }

    public string LogDirectory { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(AudioDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public string NewAudioPath(DateTimeOffset at) =>
        Path.Combine(AudioDirectory, $"{at:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..(15 + 1 + 8)] + ".wav");
}
