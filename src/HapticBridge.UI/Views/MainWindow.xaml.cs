using System.IO;
using System.Security.Cryptography;
using System.Windows;
using HapticBridge.Integrations.Apollo;
using HapticBridge.UI.ViewModels;

namespace HapticBridge.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        RefreshAudioHostButtons();
        if (viewModel.ApolloSettings is { } apollo)
        {
            apollo.ManagementRequested += OpenManagement;
            Closed += (_, _) => apollo.ManagementRequested -= OpenManagement;
        }

        void OpenManagement(ApolloHostStatus host, ApolloManagementPage page)
        {
            if (host.CertificatePath is null && host.TrustedInstalledExecutable is null)
            {
                MessageBox.Show(this, "HapticBridge could not find the running Apollo host’s local certificate. Check its configuration or use Open Apollo web UI.",
                    "Apollo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApolloAdminClient session;
            try
            {
                session = new ApolloAdminClient(host);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
            {
                MessageBox.Show(this, "HapticBridge could not read Apollo’s local certificate. Check the host configuration, then try again.",
                    "Apollo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using var localSession = session;
            new ApolloManagementWindow(localSession, host, page, async () =>
            {
                if (!await viewModel.ApolloSettings!.WaitForHostStopAsync()) return false;
                if (viewModel.ControllerSession is { IsRunning: true } controller) await controller.StopAsync();
                return true;
            }) { Owner = this }.ShowDialog();
        }
    }

    private void RefreshAudioHostButtons()
    {
        bool imported = ApolloAudioTestHost.IsImported;
        ImportAudioHostButton.IsEnabled = !imported;
        StartAudioHostButton.IsEnabled = imported;
    }

    private async void ImportAudioHost_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select the Controller + Audio test host ZIP",
            Filter = "ZIP archives (*.zip)|*.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunAudioHostActionAsync("Verifying and importing the test host…",
            () => ApolloAudioTestHost.Import(dialog.FileName),
            "Test host imported. Stop your regular Apollo host before starting this copy.");
    }

    private async void StartAudioHost_Click(object sender, RoutedEventArgs args)
    {
        await RunAudioHostActionAsync("Starting the test host…", ApolloAudioTestHost.Start,
            "Test host launched. When Apollo is online, select Configure Controller + Audio.");
        if (DataContext is DashboardViewModel { ApolloSettings: { } apollo }) await apollo.RefreshAsync();
    }

    private async Task RunAudioHostActionAsync(string progress, Action action, string success)
    {
        ImportAudioHostButton.IsEnabled = StartAudioHostButton.IsEnabled = false;
        AudioHostNotice.Text = progress;
        try
        {
            await Task.Run(action);
            AudioHostNotice.Text = success;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AudioHostNotice.Text = exception.Message;
        }
        finally { RefreshAudioHostButtons(); }
    }
}
