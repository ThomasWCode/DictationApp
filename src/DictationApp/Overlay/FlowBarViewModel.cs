using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DictationApp.Core.Cleanup;
using DictationApp.Core.Session;

namespace DictationApp.Overlay;

public sealed partial class FlowBarViewModel : ObservableObject
{
    private readonly DictationStatusHub _hub;

    [ObservableProperty]
    private DictationState _state = DictationState.Idle;

    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private string _liveText = string.Empty;

    [ObservableProperty]
    private float _level;

    [ObservableProperty]
    private Tone _tone;

    [ObservableProperty]
    private CleanupLevel _cleanupLevel;

    [ObservableProperty]
    private string? _badge;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _chipsEnabled;

    public FlowBarViewModel(DictationStatusHub hub)
    {
        _hub = hub;
        Apply(hub.Current);
    }

    public IReadOnlyList<Tone> Tones { get; } = [Tone.Neutral, Tone.Formal, Tone.Casual];

    public IReadOnlyList<CleanupLevel> Levels { get; } = [CleanupLevel.None, CleanupLevel.Light, CleanupLevel.Medium, CleanupLevel.High];

    public void Apply(DictationStatus status)
    {
        State = status.State;
        LiveText = status.LiveText;
        Level = status.Level;
        Tone = status.Tone;
        CleanupLevel = status.CleanupLevel;
        Badge = status.Badge;
        IsRecording = status.State is DictationState.Recording;
        IsBusy = status.State is DictationState.Finalising or DictationState.PostProcessing or DictationState.Inserting;
        ChipsEnabled = status.State is DictationState.Arming or DictationState.Recording;
        StateText = status.State switch
        {
            DictationState.Arming => "Listening…",
            DictationState.Recording => "Listening",
            DictationState.Finalising => "Finishing…",
            DictationState.PostProcessing => "Cleaning up…",
            DictationState.Inserting => "Inserting…",
            _ => status.Badge ?? string.Empty,
        };
    }

    [RelayCommand]
    private void SetTone(Tone tone) => _hub.RequestTone(tone);

    [RelayCommand]
    private void SetLevel(CleanupLevel level) => _hub.RequestLevel(level);
}
