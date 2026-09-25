using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DictationApp.Core.Abstractions;
using DictationApp.Core.History;
using DictationApp.Core.Session;
using DictationApp.Core.Settings;
using DictationApp.Windows.Audio;
using Microsoft.Extensions.Logging;

namespace DictationApp.History;

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly IHistoryRepository _history;
    private readonly DictationOrchestrator _orchestrator;
    private readonly IClipboard _clipboard;
    private readonly ITextInserter _inserter;
    private readonly IForegroundContextProvider _foreground;
    private readonly WavPlayer _player;
    private readonly ISettingsStore _settings;
    private readonly ILogger<HistoryViewModel> _logger;

    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private DictationRecord? _selected;
    [ObservableProperty] private string _footer = string.Empty;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    public HistoryViewModel(
        IHistoryRepository history,
        DictationOrchestrator orchestrator,
        IClipboard clipboard,
        ITextInserter inserter,
        IForegroundContextProvider foreground,
        WavPlayer player,
        ISettingsStore settings,
        ILogger<HistoryViewModel> logger)
    {
        _history = history;
        _orchestrator = orchestrator;
        _clipboard = clipboard;
        _inserter = inserter;
        _foreground = foreground;
        _player = player;
        _settings = settings;
        _logger = logger;
        _player.PlaybackStopped += () => Application.Current?.Dispatcher.BeginInvoke(() => IsPlaying = false);
    }

    public event Action? RequestHide;

    public ObservableCollection<DictationRecord> Records { get; } = [];

    public bool CanRetry => Selected is { Status: RecordStatus.Failed or RecordStatus.Pending, HasAudio: true };

    public bool CanUndo => Selected is { CanUndoAiEdit: true };

    public bool CanPlay => Selected is { HasAudio: true } s && File.Exists(s.AudioPath);

    partial void OnSelectedChanged(DictationRecord? value)
    {
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanPlay));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var selectedId = Selected?.Id;
            var records = await _history.SearchAsync(string.IsNullOrWhiteSpace(Query) ? null : Query, 500);
            Records.Clear();
            foreach (var r in records)
            {
                Records.Add(r);
            }

            Selected = Records.FirstOrDefault(r => r.Id == selectedId) ?? Records.FirstOrDefault();
            var stats = await _history.GetStatsAsync();
            Footer = $"{stats.Count} dictation{(stats.Count == 1 ? string.Empty : "s")} · {stats.WithAudio} with audio · estimated cost ${stats.TotalCost:0.00}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "History refresh failed");
            Status = "Could not load history: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void PlayOrStop()
    {
        if (IsPlaying)
        {
            _player.Stop();
            IsPlaying = false;
            return;
        }

        if (Selected?.AudioPath is { } path && File.Exists(path))
        {
            try
            {
                _player.Play(path);
                IsPlaying = true;
            }
            catch (Exception ex)
            {
                Status = "Playback failed: " + ex.Message;
            }
        }
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (Selected is null)
        {
            return;
        }

        await _clipboard.SetTextAsync(Selected.InsertedText.Length > 0 ? Selected.InsertedText : Selected.RawTranscript);
        Status = "Copied to clipboard.";
    }

    [RelayCommand]
    private async Task ReInsertAsync()
    {
        if (Selected is null)
        {
            return;
        }

        var text = Selected.InsertedText.Length > 0 ? Selected.InsertedText : Selected.RawTranscript;
        RequestHide?.Invoke();
        await Task.Delay(350);
        try
        {
            var target = _foreground.Capture();
            if (target.IsEditable && !target.IsElevated)
            {
                // The destination app's rule decides the paste keystroke, as for a live dictation.
                await _inserter.InsertAsync(text, target, DictationOrchestrator.PasteModeFor(target, _settings.Current), CancellationToken.None);
                Status = $"Re-inserted into {target.ProcessName}.";
            }
            else
            {
                await _clipboard.SetTextAsync(text);
                Status = "No editable target; copied to clipboard.";
            }
        }
        catch (Exception ex)
        {
            Status = "Re-insert failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is null)
        {
            return;
        }

        var record = Selected;
        _player.Stop();
        IsPlaying = false;
        await _history.DeleteAsync(record.Id);
        if (record.AudioPath is { } path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not delete audio {Path}", path);
            }
        }

        Records.Remove(record);
        Selected = Records.FirstOrDefault();
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (Selected is null || !CanRetry)
        {
            return;
        }

        IsBusy = true;
        Status = "Re-streaming audio…";
        try
        {
            var ok = await _orchestrator.RetryAsync(Selected.Id, CancellationToken.None);
            Status = ok ? "Retry complete; text copied to the clipboard." : "Retry did not produce text.";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    /// <summary>Puts the raw transcript back: copies it and marks the record so the History shows what happened.</summary>
    [RelayCommand]
    private async Task UndoAiEditAsync()
    {
        if (Selected is null || !CanUndo)
        {
            return;
        }

        var record = Selected;
        await _clipboard.SetTextAsync(record.RawTranscript);
        record.InsertedText = record.RawTranscript;
        record.AiEditUndone = true;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await _history.UpdateAsync(record);
        Status = "Raw transcript copied to the clipboard; paste it over the AI version.";
        await RefreshAsync();
    }

    public void Cleanup()
    {
        _player.Stop();
        IsPlaying = false;
    }
}
