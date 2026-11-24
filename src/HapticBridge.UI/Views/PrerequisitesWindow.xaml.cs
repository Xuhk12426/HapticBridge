using System.Windows;
using HapticBridge.UI.ViewModels;

namespace HapticBridge.UI.Views;

public partial class PrerequisitesWindow : Window
{
    public PrerequisitesWindow(PrerequisitesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += (_, args) => args.Cancel = viewModel.IsBusy;
        Loaded += CheckOnLoad;

        async void CheckOnLoad(object sender, RoutedEventArgs args)
        {
            Loaded -= CheckOnLoad;
            await viewModel.CheckAsync();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs args) => Close();
}
