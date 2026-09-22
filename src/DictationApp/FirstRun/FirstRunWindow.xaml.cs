using System.Windows;
using DictationApp.Core.Settings;

namespace DictationApp.FirstRun;

public partial class FirstRunWindow : Window
{
    private readonly FirstRunViewModel _viewModel;
    private readonly ISettingsStore _settings;

    public FirstRunWindow(FirstRunViewModel viewModel, ISettingsStore settings)
    {
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.RequestClose += Close;
        Closed += (_, _) => viewModel.Dispose();
    }

    private void KeyBox_OnPasswordChanged(object sender, RoutedEventArgs e) => _viewModel.ApiKey = KeyBox.Password;

    private async void Skip_OnClick(object sender, RoutedEventArgs e)
    {
        await _settings.UpdateAsync(s => s.FirstRunCompleted = true);
        Close();
    }
}
