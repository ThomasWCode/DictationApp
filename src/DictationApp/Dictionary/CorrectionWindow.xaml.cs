using System.Windows;

namespace DictationApp.Dictionary;

public partial class CorrectionWindow : Window
{
    public CorrectionWindow(CorrectionViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.RequestClose += Close;
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }
}
