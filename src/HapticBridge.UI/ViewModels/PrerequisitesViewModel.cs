using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using HapticBridge.Integrations.SoundSync;
using HapticBridge.Setup.Prerequisites;

namespace HapticBridge.UI.ViewModels;

public sealed class PrerequisitesViewModel : ViewModelBase
{
    private readonly PrerequisiteInstaller _installer;
    private readonly SetupRoute _route;
    private CancellationTokenSource? _installation;
    private bool _isBusy;
    private string _status = "Checking this PC…";
    private IReadOnlyList<PrerequisiteItem> _items = [];

    public PrerequisitesViewModel(PrerequisiteInstaller installer, SetupRoute route, SoundSyncIntegration soundSync)
    {
        _installer = installer;
        _route = route;
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !IsBusy);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () =>
            !IsBusy && Items.Any(item => item.State == PrerequisiteState.Missing));
        StopCommand = new RelayCommand(() =>
        {
            _installation?.Cancel();
            Status = "Stopping. Close any open vendor installer to finish.";
            CommandManager.InvalidateRequerySuggested();
        }, () => _installation is { IsCancellationRequested: false });
        OpenSourceCommand = new RelayCommand(source =>
        {
            if (source is not string url) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                Status = "Windows could not open the vendor page.";
            }
        }, source => !IsBusy && source is string url && Items.Any(item => item.Source == url));
        OpenSoundSyncCommand = new RelayCommand(soundSync.LaunchSoundSync, () =>
            !IsBusy && IsExternal && Items.Any(item => item.Name == "SoundSync" && item.State == PrerequisiteState.Installed));
    }

    public bool IsExternal => _route == SetupRoute.ExternalTools;
    public string Title => IsExternal ? "Set up External tools" : "Set up DualSense Haptics";
    public string NextSteps => IsExternal
        ? "After setup, open each tool to finish its first-run prompts. If SoundSync has no saved profile, open it once and close it. Then run Setup / Repair and apply the listed changes. On your phone, pair Artemis with Apollo and enable Audio to Haptics."
        : "Finish Apollo’s first-run setup and select DS4 controller emulation. Pair Artemis with Apollo on your phone and enable Audio to Haptics. Keep DS4Windows closed and return here to enable DualSense haptics from the dashboard.";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public IReadOnlyList<PrerequisiteItem> Items { get => _items; private set => SetProperty(ref _items, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsInstalling => _installation is not null;
    public ICommand CheckCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand OpenSourceCommand { get; }
    public ICommand OpenSoundSyncCommand { get; }

    internal async Task CheckAsync()
    {
        IsBusy = true;
        Status = "Checking this PC…";
        try
        {
            await RefreshAsync();
            Status = Summarize();
        }
        finally { IsBusy = false; }
    }

    private async Task InstallAsync()
    {
        string[] approved = Items.Where(item => item.State == PrerequisiteState.Missing)
            .Select(item => item.Name).ToArray();
        using var cancellation = new CancellationTokenSource();
        _installation = cancellation;
        OnPropertyChanged(nameof(IsInstalling));
        IsBusy = true;
        Status = "Starting setup…";
        var progress = new Progress<string>(message =>
        {
            if (_installation == cancellation && !cancellation.IsCancellationRequested) Status = message;
        });

        string? stopped = null;
        try
        {
            await Task.Run(() => _installer.InstallAsync(_route, approved, progress, cancellation.Token));
        }
        catch (OperationCanceledException exception)
        {
            stopped = cancellation.IsCancellationRequested ? "Setup stopped." : exception.Message;
        }
        catch (Exception exception)
        {
            stopped = exception.Message;
        }
        finally
        {
            _installation = null;
            OnPropertyChanged(nameof(IsInstalling));
            try
            {
                await RefreshAsync();
                Status = stopped is null ? Summarize() : $"{stopped} {Summarize()}";
            }
            finally { IsBusy = false; }
        }
    }

    private async Task RefreshAsync() => Items = await Task.Run(() => _installer.Inspect(_route));

    private string Summarize()
    {
        int missing = Items.Count(item => item.State == PrerequisiteState.Missing);
        bool needsAttention = Items.Any(item => item.State == PrerequisiteState.NeedsAttention);
        string restart = Items.Any(item => item.State == PrerequisiteState.RestartRequired)
            ? " Restart Windows to finish the driver setup." : "";
        string summary = missing switch
        {
            1 => "One prerequisite is available to install.",
            > 1 => $"{missing} prerequisites are available to install.",
            _ when !needsAttention && restart.Length == 0 => "All Windows prerequisites were found.",
            _ => "Checked Windows prerequisites."
        };
        return summary + (needsAttention ? " Review the items marked Needs attention." : "") + restart;
    }
}
