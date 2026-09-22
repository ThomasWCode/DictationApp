using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;
using DictationApp.Core.Settings;

namespace DictationApp.Settings;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly Shell _shell;
    private readonly HashSet<int> _chordKeysDown = [];
    private readonly List<int> _chordRecorded = [];

    public SettingsWindow(SettingsViewModel viewModel, Shell shell)
    {
        _viewModel = viewModel;
        _shell = shell;
        DataContext = viewModel;
        InitializeComponent();
        ToneColumn.ItemsSource = Enum.GetValues<Tone>();
        LevelColumn.ItemsSource = Enum.GetValues<CleanupLevel>();
        PasteColumn.ItemsSource = Enum.GetValues<PasteMode>();
        viewModel.RequestClose += Close;
        Closed += (_, _) => viewModel.StopMicTest();
    }

    private void ApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e) => _viewModel.ApiKey = ApiKeyBox.Password;

    /// <summary>
    /// Chord recorder: keys held together are collected until the last one is released, then the chord is
    /// written as text. Typing the name by hand still works because the TextBox stays editable.
    /// </summary>
    private void ChordBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab or Key.Escape or Key.Enter)
        {
            return;
        }

        var vk = HotkeyChord.Normalise(KeyInterop.VirtualKeyFromKey(key));
        if (vk == 0)
        {
            return;
        }

        e.Handled = true;
        if (_chordKeysDown.Count == 0)
        {
            _chordRecorded.Clear();
        }

        if (_chordKeysDown.Add(vk) && !_chordRecorded.Contains(vk) && _chordRecorded.Count < HotkeyChord.MaxKeys)
        {
            _chordRecorded.Add(vk);
        }

        try
        {
            ChordBox.Text = new HotkeyChord(_chordRecorded).ToString();
        }
        catch (ArgumentException)
        {
        }
    }

    private void ChordBox_OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = HotkeyChord.Normalise(KeyInterop.VirtualKeyFromKey(key));
        _chordKeysDown.Remove(vk);
        if (vk != 0)
        {
            e.Handled = true;
        }
    }

    private void NewTerm_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.AddTermCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void CorrectLast_OnClick(object sender, RoutedEventArgs e) => _shell.ShowCorrection();
}
