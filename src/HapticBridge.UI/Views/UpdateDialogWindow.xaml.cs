using System.Windows;
using HapticBridge.UI.ViewModels;

namespace HapticBridge.UI.Views;

public partial class UpdateDialogWindow : Window
{
    public UpdateDialogWindow(UpdateConfirmationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += Close;
    }
}
