using System.Windows;
using HapticBridge.UI.ViewModels;

namespace HapticBridge.UI.Views;

public partial class DiagnosticsWindow : Window
{
    public event Action? RequestNativeTests;

    public DiagnosticsWindow(DiagnosticsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadReportAsync();
    }

    private void NativeTests_Click(object sender, RoutedEventArgs args) => RequestNativeTests?.Invoke();

    private async void SaveReport_Click(object sender, RoutedEventArgs args)
    {
        var model = (DiagnosticsViewModel)DataContext;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save diagnostic report",
            FileName = $"HapticBridge-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExt = ".txt",
            Filter = "Text files (*.txt)|*.txt"
        };
        if (dialog.ShowDialog(this) == true) await model.SaveReportAsync(dialog.FileName);
    }
}
