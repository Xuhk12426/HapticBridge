using System.Windows;
using HapticBridge.UI.ViewModels;

namespace HapticBridge.UI.Views;

public partial class SetupRepairWindow : Window
{
    private readonly SetupRepairViewModel _viewModel;

    public SetupRepairWindow(SetupRepairViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += InspectOnLoad;
        Closing += (_, args) => args.Cancel = viewModel.IsApplying;
    }

    private async void InspectOnLoad(object sender, RoutedEventArgs args)
    {
        Loaded -= InspectOnLoad;
        await _viewModel.CheckAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs args) => Close();
}
