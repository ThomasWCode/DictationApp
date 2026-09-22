using System.Windows;
using System.Windows.Input;

namespace DictationApp.History;

public partial class HistoryWindow : Window
{
    private readonly HistoryViewModel _viewModel;

    public HistoryWindow(HistoryViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.RequestHide += () => WindowState = WindowState.Minimized;
        Loaded += async (_, _) => await viewModel.RefreshAsync();
        Closed += (_, _) => viewModel.Cleanup();
    }

    private async void SearchBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _viewModel.RefreshAsync();
        }
    }
}
