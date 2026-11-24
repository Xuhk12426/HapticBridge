using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using HapticBridge.Integrations.Apollo;

namespace HapticBridge.UI.ViewModels;

public enum ApolloManagementPage { Devices, Audio, Stop }

public sealed class ApolloSettingsViewModel : ViewModelBase
{
    private readonly Func<Task<ApolloHostStatus>> _inspect;
    private readonly Action<Uri> _open;
    private readonly Action? _start;
    private readonly Action<string> _copy;
    private ApolloHostStatus? _host;
    private string _status = "Checking Apollo…";
    private bool _checking;
    private bool _starting;
    private bool _configuring;
    private string _hostStatus = "Checking Apollo…";
    private string _actionStatus = string.Empty;

    public ApolloSettingsViewModel(ApolloHostClient apollo)
        : this(apollo.InspectAsync, uri =>
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(),
            apollo.StartInstalledHost)
    {
    }

    internal ApolloSettingsViewModel(Func<Task<ApolloHostStatus>> inspect, Action<Uri> open,
        Action? start = null, Action<string>? copy = null)
    {
        _inspect = inspect;
        _open = open;
        _start = start;
        _copy = copy ?? Clipboard.SetText;
        StartHostCommand = new AsyncRelayCommand(StartHostAsync, () => CanStart);
        ConfigureDs4Command = new AsyncRelayCommand(ConfigureDs4Async,
            () => !_checking && !_starting && !_configuring && _host?.ConfigurationPath is not null);
        OpenPairingCommand = new RelayCommand(() => Manage(ApolloManagementPage.Devices), () => HostReady);
        StopHostCommand = new RelayCommand(() => Manage(ApolloManagementPage.Stop), () => HostReady);
        CopyAddressCommand = new RelayCommand(CopyAddress, () => _host?.Addresses.Count > 0);
        OpenModeSettingsCommand = new RelayCommand(() => Manage(ApolloManagementPage.Audio), () =>
            _host?.AudioMode is ApolloAudioMode.Enabled or ApolloAudioMode.Available);
        OpenWebUiCommand = new RelayCommand(() => Open(_host!.WebUi), () => !_checking && _host is not null &&
            (_host.IsInstalled || _host.AudioMode != ApolloAudioMode.Unreachable));
    }

    public ICommand OpenModeSettingsCommand { get; }
    public ICommand OpenWebUiCommand { get; }
    public ICommand StartHostCommand { get; }
    public ICommand ConfigureDs4Command { get; }
    public ICommand StopHostCommand { get; }
    public ICommand OpenPairingCommand { get; }
    public ICommand CopyAddressCommand { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string HostStatus { get => _hostStatus; private set => SetProperty(ref _hostStatus, value); }
    public string ActionStatus { get => _actionStatus; private set => SetProperty(ref _actionStatus, value); }
    public string Addresses => _host is { Addresses.Count: > 0 } host
        ? string.Join("  •  ", host.Addresses) : "No Wi-Fi or Ethernet IPv4 address found.";
    private bool HostReady => _host is not null && _host.AudioMode != ApolloAudioMode.Unreachable;
    private bool CanStart => !_starting && !_configuring && _start is not null &&
        _host is { CanStart: true, IsRunning: false, AudioMode: ApolloAudioMode.Unreachable };

    public event Action<ApolloHostStatus, ApolloManagementPage>? ManagementRequested;

    private void Manage(ApolloManagementPage page)
    {
        if (HostReady) ManagementRequested?.Invoke(_host!, page);
    }

    internal async Task<bool> WaitForHostStopAsync()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await RefreshAsync();
            if (_host is { IsRunning: false, AudioMode: ApolloAudioMode.Unreachable }) return true;
            await Task.Delay(500);
        }
        return false;
    }

    internal async Task RefreshAsync()
    {
        if (_checking) return;
        _checking = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            _host = await Task.Run(_inspect);
            OnPropertyChanged(nameof(Addresses));
            HostStatus = HostReady
                ? string.IsNullOrWhiteSpace(_host.HostName) ? "Apollo is ready to pair" : $"Apollo is online · {_host.HostName}"
                : _host.IsRunning ? "Apollo is running but hasn’t responded"
                : _host.CanStart ? "Apollo is stopped" : "Apollo wasn’t found";
            Status = _host.AudioMode switch
            {
                ApolloAudioMode.Enabled => "Controller + Audio is on. Launch Remote Input in Artemis.",
                ApolloAudioMode.Available => "Configure Controller + Audio here, then restart Apollo between sessions to apply it.",
                ApolloAudioMode.Unsupported => "This Apollo host needs the Controller + Audio test build to stream audio in Remote Input.",
                _ => "Start Apollo to check Controller + Audio."
            };
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or
            Win32Exception or InvalidOperationException)
        {
            _host = null;
            OnPropertyChanged(nameof(Addresses));
            HostStatus = "Apollo status unavailable. Select Refresh to try again.";
            Status = string.Empty;
        }
        finally
        {
            _checking = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    internal async Task StartHostAsync()
    {
        if (!CanStart) return;
        _starting = true;
        ActionStatus = "Starting Apollo. If Windows asks, approve Apollo’s startup prompt.";
        try
        {
            var host = _host!;
            if (host.ConfigurationPath is string path && System.IO.File.Exists(path))
                await Task.Run(() => ApolloDs4Setup.Configure(host));
            await Task.Run(_start!);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                await RefreshAsync();
                if (HostReady)
                {
                    ActionStatus = string.Empty;
                    return;
                }
                if (attempt < 5) await Task.Delay(1000);
            }
            ActionStatus = "Apollo hasn’t responded yet. Check its startup window, then select Refresh.";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            ActionStatus = $"Could not start Apollo. {exception.Message}";
        }
        finally
        {
            _starting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ConfigureDs4Async()
    {
        if (_host is not { ConfigurationPath: not null } host || _configuring) return;
        _configuring = true;
        ActionStatus = "Setting Apollo to use a DualShock 4. Approve the Windows prompt if shown.";
        try
        {
            await Task.Run(() => ApolloDs4Setup.Configure(host));
            ActionStatus = host.IsRunning
                ? "DualShock 4 is saved. Stop and start Apollo between streams, then reconnect Artemis."
                : "DualShock 4 is saved. Start Apollo, then connect Artemis.";
        }
        catch (Exception exception) when (exception is Win32Exception or System.IO.IOException or UnauthorizedAccessException)
        {
            ActionStatus = $"Could not configure DualShock 4. {exception.Message}";
        }
        finally
        {
            _configuring = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void CopyAddress()
    {
        if (_host?.Addresses.FirstOrDefault() is not string address) return;
        try
        {
            _copy(address);
            ActionStatus = $"Copied {address}. Enter it in Artemis under Add PC.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ActionStatus = "Couldn’t copy the address. Enter it manually in Artemis.";
        }
    }

    private void Open(Uri uri)
    {
        try { _open(uri); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            ActionStatus = $"Windows could not open Apollo in the browser. Open {uri.AbsoluteUri}";
        }
    }
}
