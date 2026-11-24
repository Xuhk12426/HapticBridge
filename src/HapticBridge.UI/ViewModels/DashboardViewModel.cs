using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using HapticBridge.Audio.Wasapi;
using HapticBridge.Core.Models;
using HapticBridge.Core.Services;
using HapticBridge.Setup.Engine;
using HapticBridge.Setup.Prerequisites;

namespace HapticBridge.UI.ViewModels;

public class DashboardViewModel : ViewModelBase
{
    internal enum DashboardPage { Native, ExternalTools, Testing, Apollo }

    private const int AudioReadbackDelayMilliseconds = 500;
    private readonly IAudioEndpointService _audioService;
    private readonly IVoicemeeterManager _voicemeeter;
    private readonly IComponentDetectionService _componentDetection;
    private readonly IControllerDetectionService _controllerDetection;
    private readonly DispatcherTimer _refreshTimer;
    private readonly SetupRepairEngine? _setup;
    private readonly WasapiStereoPlayback? _stereoPlayback;

    private string _systemStatusText = "Checking...";
    private ComponentHealthState _systemStatusHealth = ComponentHealthState.Disconnected;
    private string _controllerMainStatus = "Checking...";
    private string _controllerSubtitle = "Enumerating controllers...";
    private string _hidHideStatus = "Checking HidHide…";
    private SetupRepairPlan? _hidHidePlan;
    private bool _repairingHidHide;
    private bool _hidHideRepaired;
    private AudioEndpointModel? _selectedAudioDevice;
    private string? _nativeListeningDeviceId;
    private DashboardPage _page;
    private bool _externalToolsBeforeApollo;
    private string? _pendingAudioOutput;
    private string _audioOutputStatus = string.Empty;
    private bool _canSelectAudioOutput;
    private bool _canAdjustListeningVolume;
    private int _listeningVolume;
    private long _nativeVolumeRevision;
    private long _audioReadbackAfter;
    private string _hapticRoutingStatus = "Checking...";
    private ComponentHealthState _hapticRoutingHealth = ComponentHealthState.Disconnected;
    private bool _isRefreshing;
    private bool _updatingAudioDevices;

    public string HapticRoutingMainValue => "Voicemeeter Banana";
    public ControllerSessionViewModel? ControllerSession { get; }
    public bool UsesExternalTools => IsExternalTools || (IsApollo && _externalToolsBeforeApollo);
    public bool UsesNativeAudio => !UsesExternalTools;
    public bool IsNative => _page == DashboardPage.Native;
    public bool IsExternalTools => _page == DashboardPage.ExternalTools;
    public bool IsTesting => _page == DashboardPage.Testing;
    public bool IsApollo => _page == DashboardPage.Apollo;
    public ApolloSettingsViewModel? ApolloSettings { get; }
    public GameFreeTestingViewModel Testing { get; }
    public string ConnectButtonText => _hidHidePlan?.HasHidHideRepair == true
        ? "Repair access and enable"
        : "Enable DualSense haptics";

    public string SystemStatusText
    {
        get => _systemStatusText;
        set => SetProperty(ref _systemStatusText, value);
    }

    public ComponentHealthState SystemStatusHealth
    {
        get => _systemStatusHealth;
        set => SetProperty(ref _systemStatusHealth, value);
    }

    public string ControllerMainStatus
    {
        get => _controllerMainStatus;
        set => SetProperty(ref _controllerMainStatus, value);
    }

    public string ControllerSubtitle
    {
        get => _controllerSubtitle;
        set => SetProperty(ref _controllerSubtitle, value);
    }

    public string HidHideStatus
    {
        get => _hidHideStatus;
        private set => SetProperty(ref _hidHideStatus, value);
    }

    public AudioEndpointModel? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (_updatingAudioDevices || !CanSelectAudioOutput || value is null || SameEndpoint(_selectedAudioDevice, value)) return;

            if (UsesNativeAudio && _stereoPlayback is not null)
            {
                _nativeListeningDeviceId = value.Id;
                UpdateNativeListening(_stereoPlayback, value, false);
                return;
            }

            if (FindListeningDevice(value.FriendlyName) is null)
            {
                AudioOutputStatus = "Voicemeeter needs a unique device name. Rename one of these outputs in Windows.";
                OnPropertyChanged();
                return;
            }

            if (!_voicemeeter.TrySetPhysicalOutput(value.FriendlyName))
            {
                AudioOutputStatus = "Voicemeeter could not switch the listening output.";
                OnPropertyChanged();
                return;
            }

            _pendingAudioOutput = value.FriendlyName;
            _audioReadbackAfter = Environment.TickCount64 + AudioReadbackDelayMilliseconds;
            CanAdjustListeningVolume = false;
            SetProperty(ref _selectedAudioDevice, value);
            AudioOutputStatus = $"Switching to {value.FriendlyName}…";
        }
    }

    public bool CanSelectAudioOutput
    {
        get => _canSelectAudioOutput;
        private set => SetProperty(ref _canSelectAudioOutput, value);
    }

    public bool CanAdjustListeningVolume
    {
        get => _canAdjustListeningVolume;
        private set
        {
            if (SetProperty(ref _canAdjustListeningVolume, value)) OnPropertyChanged(nameof(ListeningVolumeText));
        }
    }

    public int ListeningVolume
    {
        get => _listeningVolume;
        set
        {
            if (_updatingAudioDevices || !CanAdjustListeningVolume || _selectedAudioDevice is null ||
                value is < 0 or > 100 || value == _listeningVolume) return;

            if (UsesNativeAudio && _stereoPlayback is not null)
            {
                var selected = _selectedAudioDevice;
                _nativeVolumeRevision++;
                if (_audioService.SetPhysicalPlaybackVolume(selected.Id, value) is { } readback)
                {
                    selected.VolumeLevel = readback.Level;
                    selected.IsMuted = readback.IsMuted;
                    UpdateNativeListening(_stereoPlayback, selected, false);
                    if (value != _listeningVolume) OnPropertyChanged();
                    return;
                }

                CanAdjustListeningVolume = false;
                AudioOutputStatus = $"Windows could not change the volume for {selected.FriendlyName}.";
                OnPropertyChanged();
                return;
            }

            if (!_voicemeeter.TrySetListeningVolume(_selectedAudioDevice.FriendlyName, value))
            {
                CanAdjustListeningVolume = false;
                AudioOutputStatus = "Voicemeeter could not adjust this output. Checking the live route…";
                OnPropertyChanged();
                return;
            }

            _audioReadbackAfter = Environment.TickCount64 + AudioReadbackDelayMilliseconds;
            UpdateListeningVolume(value);
            AudioOutputStatus = string.Empty;
        }
    }

    public string ListeningVolumeText => CanAdjustListeningVolume ? $"{ListeningVolume}%" : "—";

    public string AudioOutputStatus
    {
        get => _audioOutputStatus;
        private set => SetProperty(ref _audioOutputStatus, value);
    }

    public string HapticRoutingStatus
    {
        get => _hapticRoutingStatus;
        set => SetProperty(ref _hapticRoutingStatus, value);
    }

    public ComponentHealthState HapticRoutingHealth
    {
        get => _hapticRoutingHealth;
        set => SetProperty(ref _hapticRoutingHealth, value);
    }

    public ObservableCollection<AudioEndpointModel> PhysicalAudioDevices { get; } = new();
    public ObservableCollection<SystemComponentItem> SystemComponents { get; } = new();

    public ICommand SetupRepairCommand { get; }
    public ICommand OpenPrerequisitesCommand { get; }
    public ICommand OpenVoicemeeterCommand { get; }
    public ICommand OpenDiagnosticsCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RepairHidHideCommand { get; }
    public ICommand ShowNativeCommand { get; }
    public ICommand ShowExternalToolsCommand { get; }
    public ICommand ShowTestingCommand { get; }
    public ICommand ShowApolloCommand { get; }

    public event Action? RequestOpenSetup;
    public event Action<SetupRoute>? RequestOpenPrerequisites;
    public event Action? RequestOpenDiagnostics;

    public DashboardViewModel(
        IAudioEndpointService audioService,
        IVoicemeeterManager voicemeeter,
        IComponentDetectionService componentDetection,
        IControllerDetectionService controllerDetection,
        SetupRepairEngine? setup = null,
        ControllerSessionViewModel? controllerSession = null,
        WasapiStereoPlayback? stereoPlayback = null,
        ApolloSettingsViewModel? apolloSettings = null)
    {
        _audioService = audioService;
        _voicemeeter = voicemeeter;
        _componentDetection = componentDetection;
        _controllerDetection = controllerDetection;
        _setup = setup;
        _stereoPlayback = stereoPlayback;
        ApolloSettings = apolloSettings;
        _externalToolsBeforeApollo = stereoPlayback is null;
        _page = apolloSettings is not null ? DashboardPage.Apollo :
            stereoPlayback is null ? DashboardPage.ExternalTools : DashboardPage.Native;
        ControllerSession = controllerSession;
        Testing = new GameFreeTestingViewModel(audioService, controllerDetection, controllerSession,
            () => CanSelectAudioOutput ? SelectedAudioDevice : null);
        if (controllerSession is not null && stereoPlayback is not null)
        {
            controllerSession.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(ControllerSessionViewModel.IsRunning)) return;
                CommandManager.InvalidateRequerySuggested();
                if (controllerSession.IsRunning && UsesNativeAudio)
                    UpdateNativeListening(stereoPlayback, SelectNativeListeningDevice(PhysicalAudioDevices), false);
                else
                    stereoPlayback.SelectOutput(null);
                _ = RefreshAllAsync();
            };
        }

        SetupRepairCommand = new RelayCommand(() => RequestOpenSetup?.Invoke(), () => IsExternalTools);
        OpenPrerequisitesCommand = new RelayCommand(
            () => RequestOpenPrerequisites?.Invoke(IsExternalTools ? SetupRoute.ExternalTools : SetupRoute.DualSenseHaptics),
            () => !IsTesting && ControllerSession?.IsRunning != true);
        OpenVoicemeeterCommand = new RelayCommand(() => _voicemeeter.LaunchVoicemeeter(), () => IsExternalTools);
        OpenDiagnosticsCommand = new RelayCommand(() => RequestOpenDiagnostics?.Invoke(), () => IsExternalTools);
        RefreshCommand = new AsyncRelayCommand(() => IsApollo ? ApolloSettings!.RefreshAsync() : RefreshAllAsync());
        RepairHidHideCommand = new AsyncRelayCommand(RepairHidHideAsync, () => !_repairingHidHide && _hidHidePlan is { HasHidHideRepair: true });
        ShowNativeCommand = new AsyncRelayCommand(() => SwitchPageAsync(DashboardPage.Native), () => CanSwitchPage(DashboardPage.Native));
        ShowExternalToolsCommand = new AsyncRelayCommand(() => SwitchPageAsync(DashboardPage.ExternalTools), () => CanSwitchPage(DashboardPage.ExternalTools));
        ShowTestingCommand = new AsyncRelayCommand(() => SwitchPageAsync(DashboardPage.Testing), () => CanSwitchPage(DashboardPage.Testing));
        ShowApolloCommand = new AsyncRelayCommand(() => SwitchPageAsync(DashboardPage.Apollo), () => CanSwitchPage(DashboardPage.Apollo));

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (IsApollo) await ApolloSettings!.RefreshAsync();
            await RefreshAllAsync();
        };
    }

    public void StartMonitoring()
    {
        if (IsApollo) _ = ApolloSettings!.RefreshAsync();
        _ = RefreshAllAsync();
        _refreshTimer.Start();
    }

    public void StopMonitoring()
    {
        _refreshTimer.Stop();
        Testing.Stop();
    }

    public async Task RefreshAllAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        bool externalToolsOnly = UsesExternalTools;
        bool audioInspected = false;
        long nativeVolumeRevision = _nativeVolumeRevision;

        try
        {
            if (!externalToolsOnly && _setup is not null && !_repairingHidHide)
            {
                var plan = await Task.Run(_setup.InspectHidHide);
                if (!_repairingHidHide) UpdateHidHideStatus(plan);
            }

            var (ctrlResult, physicalEndpoints, listeningOutput, audioReadTime) = await Task.Run(() =>
            {
                var controllers = _controllerDetection.DetectControllers();
                var endpoints = _audioService.GetPhysicalPlaybackEndpoints();
                long readTime = Environment.TickCount64;
                return (controllers, endpoints, externalToolsOnly ? _voicemeeter.GetListeningOutput() : null, readTime);
            });
            if (externalToolsOnly != UsesExternalTools) return;

            ControllerMainStatus = ctrlResult.MainStatus;
            ControllerSubtitle = ctrlResult.Subtitle;
            if (externalToolsOnly || nativeVolumeRevision == _nativeVolumeRevision)
                RefreshAudioOutput(physicalEndpoints, listeningOutput, audioReadTime);
            audioInspected = true;

            if (!externalToolsOnly) return;
            var components = await _componentDetection.DetectAllComponentsAsync();
            if (!UsesExternalTools) return;

            var voicemeeter = components.FirstOrDefault(component => component.Name == HapticRoutingMainValue);
            HapticRoutingStatus = voicemeeter?.StatusText ?? "Routing unverified";
            HapticRoutingHealth = voicemeeter?.Health ?? ComponentHealthState.NeedsAttention;

            SystemComponents.Clear();
            foreach (var comp in components)
            {
                SystemComponents.Add(comp);
            }

            EvaluateOverallStatus(ctrlResult, components);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning("HapticBridge status refresh failed: {0}", exception);
            SystemStatusText = "Status unavailable";
            SystemStatusHealth = ComponentHealthState.NeedsAttention;
            ControllerMainStatus = "Controller status unavailable";
            ControllerSubtitle = "Windows inspection failed.";
            HapticRoutingStatus = "Routing unverified";
            HapticRoutingHealth = ComponentHealthState.NeedsAttention;
            SystemComponents.Clear();
            if (!audioInspected && externalToolsOnly == UsesExternalTools)
            {
                CanSelectAudioOutput = false;
                CanAdjustListeningVolume = false;
                AudioOutputStatus = "The device check was interrupted. Select Refresh to check the listening output again.";
                CommandManager.InvalidateRequerySuggested();
            }
        }
        finally
        {
            _isRefreshing = false;
            if (externalToolsOnly != UsesExternalTools) await RefreshAllAsync();
        }
    }

    private bool CanSwitchPage(DashboardPage page) => _page != page &&
        (page == DashboardPage.Apollo
            ? ApolloSettings is not null
            : _stereoPlayback is not null && (page != DashboardPage.ExternalTools || ControllerSession?.IsRunning != true));

    internal Task SwitchPathAsync(bool externalToolsOnly) =>
        SwitchPageAsync(externalToolsOnly ? DashboardPage.ExternalTools : DashboardPage.Native);

    internal async Task SwitchPageAsync(DashboardPage page)
    {
        if (!CanSwitchPage(page)) return;
        bool usedExternalTools = UsesExternalTools;
        if (IsTesting) Testing.Stop();
        if (page == DashboardPage.Apollo) _externalToolsBeforeApollo = usedExternalTools;
        _page = page;
        if (usedExternalTools != UsesExternalTools)
        {
            _pendingAudioOutput = null;
            _audioReadbackAfter = 0;
            CanSelectAudioOutput = false;
            CanAdjustListeningVolume = false;
            SetProperty(ref _selectedAudioDevice, null, nameof(SelectedAudioDevice));
            AudioOutputStatus = "Checking listening outputs…";
        }
        OnPropertyChanged(nameof(UsesExternalTools));
        OnPropertyChanged(nameof(UsesNativeAudio));
        OnPropertyChanged(nameof(IsNative));
        OnPropertyChanged(nameof(IsExternalTools));
        OnPropertyChanged(nameof(IsTesting));
        OnPropertyChanged(nameof(IsApollo));
        CommandManager.InvalidateRequerySuggested();
        if (IsApollo) await ApolloSettings!.RefreshAsync();
        else await RefreshAllAsync();
    }

    private async Task RepairHidHideAsync()
    {
        if (_setup is null || _repairingHidHide || _hidHidePlan is not { HasHidHideRepair: true } plan) return;
        _repairingHidHide = true;
        HidHideStatus = "Repairing HidHide access…";
        try
        {
            var item = (await Task.Run(() => _setup.Apply(plan, onlyHidHide: true))).Single();
            _hidHideRepaired = item.State == SetupRepairState.Verified;
            _hidHidePlan = null;
            OnPropertyChanged(nameof(ConnectButtonText));
            HidHideStatus = item.Detail;
        }
        catch (Exception exception)
        {
            HidHideStatus = $"Could not repair HidHide access. {exception.Message}";
        }
        finally
        {
            _repairingHidHide = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void UpdateHidHideStatus(SetupRepairPlan plan)
    {
        bool hadRepair = _hidHidePlan?.HasHidHideRepair == true;
        _hidHidePlan = plan;
        if (hadRepair != plan.HasHidHideRepair) OnPropertyChanged(nameof(ConnectButtonText));
        var item = plan.Items.SingleOrDefault();
        if (item is not null && !(_hidHideRepaired && item.State == SetupRepairState.Configured))
            HidHideStatus = item.State == SetupRepairState.Planned ? "HidHide blocks this executable." : item.Detail;
        CommandManager.InvalidateRequerySuggested();
    }

    private void EvaluateOverallStatus(
        ControllerDetectionResult ctrl,
        IReadOnlyList<SystemComponentItem> components)
    {
        if (components.Any(c => c.LifecycleState == ComponentLifecycleState.NotInstalled))
        {
            SystemStatusText = "Setup required";
            SystemStatusHealth = ComponentHealthState.NeedsAttention;
        }
        else if (components.Any(c => c.LifecycleState is ComponentLifecycleState.Misconfigured or ComponentLifecycleState.Malfunctioning))
        {
            SystemStatusText = "Needs attention";
            SystemStatusHealth = ComponentHealthState.NeedsAttention;
        }
        else if (components.Any(c => c.LifecycleState == ComponentLifecycleState.InstalledDormant))
        {
            SystemStatusText = "Components inactive";
            SystemStatusHealth = ComponentHealthState.Disconnected;
        }
        else if (components.Count == 0 || components.Any(c => c.LifecycleState == ComponentLifecycleState.Unknown) ||
            ctrl.MainStatus == "Controller status unavailable")
        {
            SystemStatusText = "Status unverified";
            SystemStatusHealth = ComponentHealthState.NeedsAttention;
        }
        else
        {
            SystemStatusText = ctrl.IsVirtualDualSenseDetected && ctrl.PipelineStatus.IsDualSenseAudioEndpointPresent
                ? "Haptics unverified"
                : "Awaiting DualSense";
            SystemStatusHealth = ComponentHealthState.NeedsAttention;
        }
    }

    private void RefreshAudioOutput(IReadOnlyList<AudioEndpointModel> endpoints,
        (string DeviceName, int? VolumePercent)? listeningOutput, long audioReadTime)
    {
        _updatingAudioDevices = true;
        try
        {
            bool endpointsChanged = !PhysicalAudioDevices.Select(x => (x.Id, x.FriendlyName))
                .SequenceEqual(endpoints.Select(x => (x.Id, x.FriendlyName)));
            if (endpointsChanged)
            {
                PhysicalAudioDevices.Clear();
                foreach (var endpoint in endpoints) PhysicalAudioDevices.Add(endpoint);
            }

            if (UsesNativeAudio && _stereoPlayback is not null)
            {
                if (!endpointsChanged)
                {
                    for (int index = 0; index < endpoints.Count; index++)
                    {
                        var listed = PhysicalAudioDevices[index];
                        var fresh = endpoints[index];
                        listed.VolumeLevel = fresh.VolumeLevel;
                        listed.IsMuted = fresh.IsMuted;
                        listed.IsDefault = fresh.IsDefault;
                    }
                }
                UpdateNativeListening(_stereoPlayback, SelectNativeListeningDevice(endpoints), endpointsChanged);
                return;
            }
            if (audioReadTime < _audioReadbackAfter)
            {
                if (endpointsChanged)
                {
                    _selectedAudioDevice = PhysicalAudioDevices.FirstOrDefault(endpoint => SameEndpoint(_selectedAudioDevice, endpoint));
                    if (_selectedAudioDevice is null) CanAdjustListeningVolume = false;
                    OnPropertyChanged(nameof(SelectedAudioDevice));
                }
                return;
            }

            var selected = FindListeningDevice(listeningOutput?.DeviceName);
            if (!SetProperty(ref _selectedAudioDevice, selected, nameof(SelectedAudioDevice)) && endpointsChanged)
                OnPropertyChanged(nameof(SelectedAudioDevice));

            CanSelectAudioOutput = listeningOutput.HasValue && endpoints.Count > 0;
            int? volume = listeningOutput?.VolumePercent;
            if (selected is not null && volume.HasValue) UpdateListeningVolume(volume.Value);
            CanAdjustListeningVolume = selected is not null && volume.HasValue;

            AudioOutputStatus = (listeningOutput, selected, _pendingAudioOutput) switch
            {
                (null, _, _) => "Voicemeeter is unavailable. Start it to choose a listening device.",
                (_, _, _) when endpoints.Count == 0 => "Connect speakers or headphones to choose a listening device.",
                (_, _, string requested) when !string.Equals(selected?.FriendlyName, requested, StringComparison.OrdinalIgnoreCase)
                    => $"Voicemeeter did not confirm the switch to {requested}.",
                (_, null, _) => "Choose the speakers or headphones you want to hear game audio through.",
                _ when !CanAdjustListeningVolume => "Voicemeeter could not report the listening volume.",
                _ => string.Empty
            };
            _pendingAudioOutput = null;
        }
        finally
        {
            _updatingAudioDevices = false;
        }
    }

    private AudioEndpointModel? SelectNativeListeningDevice(IReadOnlyList<AudioEndpointModel> endpoints)
    {
        var selected = endpoints.FirstOrDefault(endpoint =>
            string.Equals(endpoint.Id, _nativeListeningDeviceId, StringComparison.OrdinalIgnoreCase))
            ?? endpoints.FirstOrDefault(endpoint => endpoint.IsDefault)
            ?? (endpoints.Count == 1 ? endpoints[0] : null);
        return selected is null ? null : PhysicalAudioDevices.FirstOrDefault(endpoint => SameEndpoint(endpoint, selected));
    }

    private void UpdateNativeListening(WasapiStereoPlayback playback, AudioEndpointModel? selected, bool endpointsChanged)
    {
        bool active = ControllerSession?.IsRunning == true;
        playback.SelectOutput(active ? selected?.Id : null);
        if (!SetProperty(ref _selectedAudioDevice, selected, nameof(SelectedAudioDevice)) && endpointsChanged)
            OnPropertyChanged(nameof(SelectedAudioDevice));
        if (IsTesting) CommandManager.InvalidateRequerySuggested();

        CanSelectAudioOutput = PhysicalAudioDevices.Count > 0;
        CanAdjustListeningVolume = selected?.IsMuted is not null;
        if (selected?.IsMuted is bool muted)
            UpdateListeningVolume(muted ? 0 : Math.Clamp((int)Math.Round(selected.VolumeLevel * 100), 0, 100));
        AudioOutputStatus = selected switch
        {
            null when PhysicalAudioDevices.Count == 0 => "Connect speakers or headphones to hear game audio.",
            null => "Choose the speakers or headphones you want to hear game audio through.",
            _ when selected.IsMuted is null => $"Windows could not read the volume for {selected.FriendlyName}.",
            _ when selected.IsMuted == true => $"Windows has muted {selected.FriendlyName}. Raise Listening volume to unmute it.",
            _ when ListeningVolume == 0 => $"Windows volume for {selected.FriendlyName} is at 0%.",
            _ when !active => IsTesting ? "Ready for the speaker test." : "Enable haptics to play game audio on this output.",
            _ when playback.Error is string error => $"Could not play game audio on {selected.FriendlyName}. {error}",
            _ when playback.RenderedFrames == 0 => $"Waiting for game audio on {selected.FriendlyName}.",
            _ => $"Playing DualSense game audio on {selected.FriendlyName}."
        };
    }

    private AudioEndpointModel? FindListeningDevice(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var matches = PhysicalAudioDevices.Where(endpoint => string.Equals(endpoint.FriendlyName, name,
            StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private void UpdateListeningVolume(int value)
    {
        if (SetProperty(ref _listeningVolume, value, nameof(ListeningVolume))) OnPropertyChanged(nameof(ListeningVolumeText));
    }

    private static bool SameEndpoint(AudioEndpointModel? current, AudioEndpointModel candidate) =>
        string.Equals(current?.Id, candidate.Id, StringComparison.OrdinalIgnoreCase);
}
