using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HapticBridge.Integrations.Apollo;
using HapticBridge.Setup.Backups;
using HapticBridge.UI.ViewModels;

namespace HapticBridge.UI.Views;

public partial class ApolloManagementWindow : Window
{
    private readonly ApolloAdminClient _client;
    private readonly ApolloHostStatus _host;
    private readonly ApolloManagementPage _page;
    private readonly Func<Task<bool>> _waitForStop;
    private bool _busy;

    public ApolloManagementWindow(ApolloAdminClient client, ApolloHostStatus host, ApolloManagementPage page,
        Func<Task<bool>> waitForStop)
    {
        _client = client;
        _host = host;
        _page = page;
        _waitForStop = waitForStop;
        InitializeComponent();
        Title = Heading.Text = page switch
        {
            ApolloManagementPage.Audio => "Controller + Audio",
            ApolloManagementPage.Stop => "Stop Apollo",
            _ => "Pair or manage devices"
        };
        Loaded += OnLoaded;
        Closing += (_, args) => args.Cancel = _busy;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        await RunAsync("Checking Apollo sign-in…", LoadPageAsync, offerSignIn: true);
    }

    private async Task LoadPageAsync()
    {
        var devices = await _client.ReadDevicesAsync();
        SignInPanel.Visibility = Visibility.Collapsed;
        switch (_page)
        {
            case ApolloManagementPage.Devices:
                DevicesPanel.Visibility = Visibility.Visible;
                ShowDevices(devices);
                break;
            case ApolloManagementPage.Audio:
                if (_host.ConfigurationPath is not string path)
                    throw new IOException("The running Apollo host’s configuration file could not be located.");
                var config = await Task.Run(() => ApolloIntegration.ReadHostConfiguration(path));
                if (!config.IsReadable)
                    throw new IOException("Windows could not read the running Apollo host’s configuration file.");
                ControllerAudio.IsChecked = config.IsEnabled("remote_input_audio");
                SeparateGrips.IsChecked = config.IsEnabled("remote_input_haptics");
                SeparateGrips.IsEnabled = ControllerAudio.IsChecked == true;
                AudioPanel.Visibility = Visibility.Visible;
                break;
            case ApolloManagementPage.Stop:
                StopPanel.Visibility = Visibility.Visible;
                break;
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs args)
    {
        string username = Username.Text.Trim();
        string password = Password.Password;
        if (username.Length == 0 || password.Length == 0)
        {
            Notice.Text = "Enter your Apollo username and password.";
            return;
        }

        Password.Clear();
        await RunAsync("Signing in…", async () =>
        {
            await _client.SignInAsync(username, password);
            await LoadPageAsync();
        });
    }

    private async void Pair_Click(object sender, RoutedEventArgs args)
    {
        await RunAsync("Sending PIN…", async () =>
        {
            string name = DeviceName.Text.Trim();
            await _client.PairAsync(Pin.Text.Trim(), name.Length == 0 ? "Artemis" : name);
            Pin.Clear();
            ShowDevices(await _client.ReadDevicesAsync());
            Notice.Text = "PIN accepted. Finish pairing on your phone, then refresh the list to check.";
        });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Checking devices…", async () => ShowDevices(await _client.ReadDevicesAsync()));

    private async void Disconnect_Click(object sender, RoutedEventArgs args)
    {
        if (DeviceList.SelectedItem is not ApolloPairedDevice { Connected: true } device ||
            !Confirm($"End the stream for {device.Name}? The device will stay paired.")) return;
        await RunAsync("Disconnecting device…", async () =>
        {
            await _client.DisconnectAsync(device.Id);
            ShowDevices(await _client.ReadDevicesAsync());
        });
    }

    private async void Forget_Click(object sender, RoutedEventArgs args)
    {
        if (DeviceList.SelectedItem is not ApolloPairedDevice device ||
            !Confirm($"Forget {device.Name}? You’ll need to pair it again to stream.")) return;
        await RunAsync("Removing pairing…", async () =>
        {
            await _client.ForgetAsync(device.Id);
            var devices = await _client.ReadDevicesAsync();
            ShowDevices(devices);
            Notice.Text = devices.Any(item => item.Id == device.Id)
                ? "Apollo still lists this device. Refresh to check again."
                : "Device removed.";
        });
    }

    private async void SaveAudio_Click(object sender, RoutedEventArgs args)
    {
        if (_host.ConfigurationPath is not string path) return;
        bool enabled = ControllerAudio.IsChecked == true;
        bool separate = SeparateGrips.IsChecked == true;
        await RunAsync("Saving Apollo settings…", async () =>
        {
            await Task.Run(() => ApolloIntegration.SaveControllerAudio(path, enabled, separate,
                source => new ConfigurationBackupService().CreateBackup("Apollo", source)));
            Notice.Text = "Settings saved. Stop and start Apollo between streams to apply them.";
        });
    }

    private async void Stop_Click(object sender, RoutedEventArgs args)
    {
        bool stopped = false;
        await RunAsync("Stopping Apollo…", async () =>
        {
            await _client.StopAsync();
            stopped = await _waitForStop();
            if (!stopped) Notice.Text = "Apollo still appears to be running. Refresh its status before trying again.";
        });
        if (stopped) Close();
    }

    private void ShowDevices(IReadOnlyList<ApolloPairedDevice> devices)
    {
        string? selected = (DeviceList.SelectedItem as ApolloPairedDevice)?.Id;
        DeviceList.ItemsSource = devices;
        DeviceList.SelectedItem = devices.FirstOrDefault(device => device.Id == selected);
        DeviceCount.Text = devices.Count == 0 ? "No devices paired." : $"{devices.Count} paired · {devices.Count(device => device.Connected)} connected";
        UpdateDeviceActions();
    }

    private void UpdateDeviceActions()
    {
        var device = DeviceList.SelectedItem as ApolloPairedDevice;
        DisconnectButton.IsEnabled = device?.Connected == true;
        ForgetButton.IsEnabled = device is not null;
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateDeviceActions();

    private void ControllerAudio_Changed(object sender, RoutedEventArgs args) =>
        SeparateGrips.IsEnabled = ControllerAudio.IsChecked == true;

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async Task RunAsync(string progress, Func<Task> action, bool offerSignIn = false)
    {
        if (_busy) return;
        _busy = true;
        ContentPanel.IsEnabled = false;
        Notice.Text = progress;
        try
        {
            await action();
            if (Notice.Text == progress) Notice.Text = string.Empty;
        }
        catch (ApolloAuthenticationException exception)
        {
            DevicesPanel.Visibility = AudioPanel.Visibility = StopPanel.Visibility = Visibility.Collapsed;
            SignInPanel.Visibility = Visibility.Visible;
            Notice.Text = offerSignIn ? "Sign in to continue. For a new Apollo account, choose Open Apollo web UI on the dashboard first."
                : exception.Message;
        }
        catch (HttpRequestException exception)
        {
            Notice.Text = exception.InnerException is IOException peerError &&
                exception.HttpRequestError == HttpRequestError.ConnectionError
                ? peerError.Message
                : exception.HttpRequestError == HttpRequestError.SecureConnectionError
                    ? "Apollo’s local HTTPS certificate or TLS handshake could not be verified. Reopen this window to retry."
                    : "Apollo’s local web service did not respond. Check that Apollo is running, then retry.";
        }
        catch (OperationCanceledException)
        {
            Notice.Text = "Apollo took too long to respond. Check the current status and try again.";
        }
        catch (JsonException)
        {
            Notice.Text = "Apollo returned an unexpected response.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or CryptographicException)
        {
            Notice.Text = exception.Message;
        }
        finally
        {
            ContentPanel.IsEnabled = true;
            _busy = false;
        }
    }
}
