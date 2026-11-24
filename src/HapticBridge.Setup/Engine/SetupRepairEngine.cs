using System.ComponentModel;
using System.Diagnostics;
using HapticBridge.Core.Models;
using HapticBridge.Core.Services;
using HapticBridge.Integrations.Apollo;
using HapticBridge.Integrations.DS4Windows;
using HapticBridge.Integrations.SoundSync;
using HapticBridge.Setup.Backups;
using HapticBridge.Setup.HidHide;

namespace HapticBridge.Setup.Engine;

public enum SetupRepairState
{
    Configured,
    Planned,
    NeedsAttention,
    Verified,
    Unverified
}

public sealed record SetupRepairItem(string Component, SetupRepairState State, string Detail, string BackupPaths = "")
{
    public string Status => State switch
    {
        SetupRepairState.Configured => "Configured",
        SetupRepairState.Planned => "Will apply",
        SetupRepairState.NeedsAttention => "Needs attention",
        SetupRepairState.Verified => "Verified",
        _ => "Unverified"
    };
}

public sealed class SetupRepairPlan
{
    internal SetupRepairPlan(List<SetupRepairItem> items, List<SetupRepairAction> actions, bool includesHidHide = true)
    {
        Items = items.AsReadOnly();
        Actions = actions.AsReadOnly();
        IncludesHidHide = includesHidHide;
    }

    public IReadOnlyList<SetupRepairItem> Items { get; }
    public int ChangeCount => Actions.Count;
    public bool HasHidHideRepair => Actions.Any(action => action.Target.Component == SetupRepairEngine.HidHideComponent);
    internal IReadOnlyList<SetupRepairAction> Actions { get; }
    internal bool IncludesHidHide { get; }
}

internal sealed record SetupRepairTarget(string Component, string Path, string EndpointId = "",
    string EndpointName = "", string FallbackOutput = "", string FallbackOutputId = "", bool Launch = false,
    bool Inverse = false);

internal sealed record SetupRepairAction(SetupRepairTarget Target, Func<SetupRepairItem> Execute);

public sealed class SetupRepairEngine
{
    internal const string HidHideComponent = "HidHide";
    private const string Apollo = "Apollo";
    private const string Ds4Windows = "DS4Windows";
    private const string Voicemeeter = "Voicemeeter Banana";
    private const string SoundSync = "SoundSync";
    private readonly ApolloIntegration _apollo;
    private readonly DS4WindowsIntegration _ds4;
    private readonly IVoicemeeterManager _voicemeeter;
    private readonly SoundSyncIntegration _soundSync;
    private readonly IAudioEndpointService _audio;
    private readonly IConfigurationBackupService _backups;
    private readonly HidHideAccessService? _hidHide;
    private readonly object _applyLock = new();

    public SetupRepairEngine(ApolloIntegration apollo, DS4WindowsIntegration ds4, IVoicemeeterManager voicemeeter,
        SoundSyncIntegration soundSync, IAudioEndpointService audio, IConfigurationBackupService backups,
        HidHideAccessService? hidHide = null)
    {
        _apollo = apollo;
        _ds4 = ds4;
        _voicemeeter = voicemeeter;
        _soundSync = soundSync;
        _audio = audio;
        _backups = backups;
        _hidHide = hidHide;
    }

    public SetupRepairPlan Inspect(bool includeHidHide = true)
    {
        var endpoints = _audio.GetAllRenderEndpoints().Where(endpoint => endpoint.IsActive && !string.IsNullOrEmpty(endpoint.Id)).ToArray();
        var cable = SelectEndpoint(endpoints, AudioEndpointType.VirtualCable, "CABLE In 16 Ch (");
        var vaio = SelectEndpoint(endpoints, AudioEndpointType.VoicemeeterVAIO, "Voicemeeter Input (");
        var speakers = endpoints.Where(endpoint => endpoint.EndpointType == AudioEndpointType.PhysicalPlayback).ToArray();
        var physical = SelectUnique(speakers.Where(endpoint => endpoint.IsDefault)) ?? SelectUnique(speakers);

        var items = new List<SetupRepairItem>();
        var actions = new List<SetupRepairAction>();

        if (includeHidHide)
        {
            var hidHidePlan = InspectHidHide();
            items.AddRange(hidHidePlan.Items);
            actions.AddRange(hidHidePlan.Actions);
        }
        Add(InspectApollo(vaio));
        Add(InspectDs4Windows());
        Add(InspectVoicemeeter(cable, physical));
        Add(InspectSoundSync(cable));
        items.Add(cable is null
            ? Attention("VB-CABLE", "This route needs one identifiable active VB-CABLE playback endpoint.")
            : Configured("VB-CABLE", cable.FriendlyName));
        items.Add(endpoints.Any(endpoint => endpoint.EndpointType == AudioEndpointType.VirtualDualSense)
            ? Configured("DualSense audio", "Audio endpoint available. Actual haptic signal flow requires an in-game test.")
            : Attention("DualSense audio", "Connect an Artemis controller session and wait for DS4Windows and VIIPER to expose the audio endpoint."));
        return new SetupRepairPlan(items, actions, includeHidHide);

        void Add((SetupRepairItem Item, SetupRepairAction? Action) row)
        {
            items.Add(row.Item);
            if (row.Action is not null) actions.Add(row.Action);
        }
    }

    public IReadOnlyList<SetupRepairItem> Apply(SetupRepairPlan approvedPlan, IProgress<string>? progress = null,
        bool onlyHidHide = false)
    {
        lock (_applyLock)
        {
            var current = onlyHidHide ? InspectHidHide() : Inspect(approvedPlan.IncludesHidHide);
            var results = new Dictionary<string, SetupRepairItem>(StringComparer.Ordinal);
            foreach (var approved in approvedPlan.Actions)
            {
                string component = approved.Target.Component;
                if (onlyHidHide && component != HidHideComponent) continue;
                var action = current.Actions.FirstOrDefault(candidate => candidate.Target == approved.Target);
                if (action is null)
                {
                    var item = current.Items.First(item => item.Component == component);
                    results[component] = item.State == SetupRepairState.Configured ? item :
                        Attention(component, "The approved target or a prerequisite changed. Check again before applying changes.");
                    continue;
                }

                progress?.Report(component);
                try { results[component] = action.Execute(); }
                catch (Exception exception)
                {
                    Trace.TraceWarning("Setup action for {0} failed: {1}", component, exception);
                    results[component] = Attention(component, exception.Message);
                }
            }

            var readback = onlyHidHide ? InspectHidHide() : Inspect(approvedPlan.IncludesHidHide);
            return readback.Items.Select(item => results.TryGetValue(item.Component, out var result) ? result :
                item.State == SetupRepairState.Planned
                    ? Attention(item.Component, "A new repair is available. Check again to review it.")
                    : item).ToArray();
        }
    }

    public SetupRepairPlan InspectHidHide()
    {
        lock (_applyLock)
        {
            if (_hidHide is null) return new([], []);
            var (item, action) = InspectHidHide(_hidHide);
            return new([item], action is null ? [] : [action]);
        }
    }

    public SetupRepairItem? PrepareNativeControllerAccess()
    {
        lock (_applyLock)
        {
            var plan = InspectHidHide();
            return (plan.HasHidHideRepair ? Apply(plan, onlyHidHide: true) : plan.Items).SingleOrDefault();
        }
    }

    private static (SetupRepairItem, SetupRepairAction?) InspectHidHide(HidHideAccessService hidHide)
    {
        HidHideAccess? access;
        try { access = hidHide.Inspect(); }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            Trace.TraceWarning("HidHide inspection failed: {0}", exception);
            return (Attention(HidHideComponent, $"Could not check HidHide. Close its configuration client and check again. {exception.Message}"), null);
        }

        if (access is null) return (Configured(HidHideComponent, "HidHide is optional. Its driver is not present."), null);
        if (!access.HidingEnabled) return (Configured(HidHideComponent, "HidHide device hiding is off."), null);
        if (access.CanReadHiddenDevices)
            return (Configured(HidHideComponent, "HidHide allows this executable to read hidden controllers."), null);

        var target = new SetupRepairTarget(HidHideComponent, hidHide.ApplicationPath, Inverse: access.Inverse);
        return Planned(target, "Allow this executable to read the Apollo controller through HidHide.", () =>
        {
            var (verified, backup) = hidHide.AllowAccess(target.Inverse);
            return verified
                ? Verified(HidHideComponent, "HidHide now allows this executable to read hidden controllers.", backup)
                : Unverified(HidHideComponent, "Could not verify the HidHide application list. Check again before retrying.", backup);
        });
    }

    private (SetupRepairItem, SetupRepairAction?) InspectApollo(AudioEndpointModel? vaio)
    {
        if (!_apollo.IsInstalled) return (Attention(Apollo, "Apollo installation unavailable."), null);
        string path = _apollo.ConfigurationPath;
        var config = _apollo.ReadConfiguration();
        if (string.IsNullOrEmpty(path) || !config.IsReadable)
            return (Attention(Apollo, "The host configuration file could not be read."), null);
        if (vaio is null) return (Attention(Apollo, "A unique Voicemeeter VAIO input is required."), null);
        if (ApolloMatches(config, vaio))
            return (_apollo.IsRunning ? Configured(Apollo, "DS4 emulation and the VAIO input are saved.")
                : Attention(Apollo, "Settings match. Start the Apollo host before connecting Artemis."), null);

        var target = new SetupRepairTarget(Apollo, path, vaio.Id, vaio.FriendlyName);
        return Planned(target, $"Set DS4 emulation and use {vaio.FriendlyName} as the virtual sink.", () =>
        {
            if (!Same(_apollo.ConfigurationPath, path)) return Attention(Apollo, "The configuration path changed.");
            string backup = _backups.CreateBackup(Apollo, path);
            if (string.IsNullOrEmpty(backup)) return BackupFailed(Apollo);
            return _apollo.WriteConfiguration("ds4", vaio.FriendlyName) && ApolloMatches(_apollo.ReadConfiguration(), vaio)
                ? Verified(Apollo, "Saved DS4 emulation and the VAIO input. Restart the host manually if it still uses its previous settings.", backup)
                : Unverified(Apollo, "Could not verify the saved host configuration. Check Apollo's file permissions.", backup);
        });
    }

    private (SetupRepairItem, SetupRepairAction?) InspectDs4Windows()
    {
        if (!_ds4.IsInstalled) return (Attention(Ds4Windows, "DS4Windows installation unavailable."), null);
        string directory = _ds4.ConfigurationDirectory;
        var config = _ds4.ReadConfiguration();
        if (!config.HasGlobalConfig || !config.HasDefaultProfile || string.IsNullOrEmpty(directory))
            return (Attention(Ds4Windows, "The global settings and Default profile must both be readable."), null);
        if (config.IsProperlyConfigured)
            return (_ds4.IsRunning && _ds4.IsViiperRunning ? Configured(Ds4Windows, "Default profile has DualSense audio haptics enabled.")
                : Attention(Ds4Windows, "Default profile matches. Start DS4Windows and its VIIPER helper."), null);
        if (_ds4.IsRunning) return (Attention(Ds4Windows, "Close DS4Windows before repairing its saved profiles."), null);

        return Planned(new SetupRepairTarget(Ds4Windows, directory),
            "Enable Moonlight input and VIIPER DualSense audio haptics and adaptive triggers in the Default profile.", () =>
        {
            if (_ds4.IsRunning || !Same(_ds4.ConfigurationDirectory, directory))
                return Attention(Ds4Windows, "The profile directory changed or DS4Windows started.");
            string settingsBackup = _backups.CreateBackup(Ds4Windows, Path.Combine(directory, "Profiles.xml"));
            if (string.IsNullOrEmpty(settingsBackup)) return BackupFailed(Ds4Windows);
            string profileBackup = _backups.CreateBackup(Ds4Windows, Path.Combine(directory, "Profiles", "Default.xml"));
            if (string.IsNullOrEmpty(profileBackup)) return BackupFailed(Ds4Windows, settingsBackup);
            string backups = $"{settingsBackup}{Environment.NewLine}{profileBackup}";
            if (_ds4.IsRunning) return Attention(Ds4Windows, "DS4Windows started while its backups were being saved.", backups);
            return _ds4.ConfigureDualSenseAndMoonlight() &&
                _ds4.ReadConfiguration() is { IsProperlyConfigured: true, DualSenseAdaptiveTriggersEnabled: true }
                ? Verified(Ds4Windows, "Saved and checked the Default profile. Start DS4Windows to use it.", backups)
                : Unverified(Ds4Windows, "Could not verify both profile files. Review them or use the backups to recover.", backups);
        });
    }

    private (SetupRepairItem, SetupRepairAction?) InspectVoicemeeter(AudioEndpointModel? cable, AudioEndpointModel? physical)
    {
        if (!_voicemeeter.IsInstalled) return (Attention(Voicemeeter, "Voicemeeter Banana installation unavailable."), null);
        bool launch = !_voicemeeter.IsRunning;
        if (launch && !_voicemeeter.CanLaunchBanana)
            return (Attention(Voicemeeter, "The Banana executable is unavailable."), null);

        if (!launch)
        {
            var routing = _voicemeeter.GetLiveRoutingState();
            if (!CanReadRouting(routing))
                return (Attention(Voicemeeter, "The Banana Remote API did not return a complete routing snapshot."), null);
            if (cable is null) return (Attention(Voicemeeter, "The VB-CABLE playback endpoint is unavailable or ambiguous."), null);
            if (RoutingMatches(routing, cable))
                return (Configured(Voicemeeter, "A1, A2 and the reference routing parameters match."), null);
            if (string.IsNullOrWhiteSpace(routing.PhysicalOutA1Device) && physical is null)
                return (Attention(Voicemeeter, "Select a physical game audio output to use for A1."), null);
        }

        var target = new SetupRepairTarget(Voicemeeter, _voicemeeter.DllPath, cable?.Id ?? "",
            cable?.FriendlyName ?? "", physical?.FriendlyName ?? "", physical?.Id ?? "", launch);
        string detail = launch ? "Start Voicemeeter Banana. " : string.Empty;
        detail += cable is null ? "Routing needs an active VB-CABLE endpoint." :
            $"Keep the current A1 output if set. Enable A1 and A2 on VAIO, clear B1/B2, and set A2 to {cable.FriendlyName} with Composite patch 11/12.";
        if (cable is not null && physical is not null) detail += $" If A1 is empty, use {physical.FriendlyName}.";
        return Planned(target, detail, () => ApplyVoicemeeter(target, cable, physical));
    }

    private SetupRepairItem ApplyVoicemeeter(SetupRepairTarget target, AudioEndpointModel? cable, AudioEndpointModel? physical)
    {
        if (!Same(_voicemeeter.DllPath, target.Path)) return Attention(Voicemeeter, "The Remote API path changed.");
        if (target.Launch) _voicemeeter.LaunchVoicemeeter();

        var routing = target.Launch
            ? WaitForRouting(CanReadRouting, TimeSpan.FromSeconds(6))
            : _voicemeeter.GetLiveRoutingState();
        if (!CanReadRouting(routing)) return Attention(Voicemeeter, "Banana did not return a complete routing snapshot.");
        if (cable is null) return Attention(Voicemeeter, "Banana is running. Check again when VB-CABLE is available.");
        if (RoutingMatches(routing, cable)) return Verified(Voicemeeter, "The live route already matches.");

        string? setA1 = null;
        if (string.IsNullOrWhiteSpace(routing.PhysicalOutA1Device))
        {
            if (physical is null) return Attention(Voicemeeter, "Select a physical output for A1.");
            setA1 = physical.FriendlyName;
        }
        string expectedA1 = setA1 ?? routing.PhysicalOutA1Device;
        string backup = Path.Combine(ConfigurationBackupService.DefaultBackupRoot, "Voicemeeter",
            $"Voicemeeter_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.xml");
        if (!_voicemeeter.CreateConfigurationBackup(backup)) return BackupFailed(Voicemeeter);
        if (!_voicemeeter.ConfigureReferenceRouting(setA1, cable.FriendlyName))
            return Unverified(Voicemeeter, "Banana rejected part of the routing request. Review the current route or recover from the backup.", backup);

        bool ExpectedRoute(VoicemeeterRoutingState state) => RoutingMatches(state, cable) && SameDeviceName(state.PhysicalOutA1Device, expectedA1);
        var readback = WaitForRouting(ExpectedRoute, TimeSpan.FromSeconds(2));
        return ExpectedRoute(readback)
            ? Verified(Voicemeeter, "Verified A1, the VB-CABLE A2 output and the Composite route.", backup)
            : Unverified(Voicemeeter, "Banana accepted the request. The live outputs or Composite route could not be verified.", backup);
    }

    private (SetupRepairItem, SetupRepairAction?) InspectSoundSync(AudioEndpointModel? cable)
    {
        if (!_soundSync.IsInstalled) return (Attention(SoundSync, "SoundSync installation unavailable."), null);
        if (cable is null) return (Attention(SoundSync, "A unique VB-CABLE playback endpoint is required."), null);
        string path = _soundSync.ProfilePath;
        if (!_soundSync.TryReadProfile(out var profile))
            return (Attention(SoundSync, "The saved SoundSync profile could not be read."), null);
        if (SoundSyncIntegration.IsCableOutputConfigured(profile, cable.Id))
            return (_soundSync.IsRunning ? Configured(SoundSync, "The saved profile targets VB-CABLE at 100%.")
                : Attention(SoundSync, "The saved output matches. Open SoundSync and connect its audio capture."), null);
        if (_soundSync.IsRunning) return (Attention(SoundSync, "Close SoundSync before repairing its saved profile."), null);

        return Planned(new SetupRepairTarget(SoundSync, path, cable.Id, cable.FriendlyName),
            $"Enable {cable.FriendlyName} at 100% in the saved SoundSync profile.", () =>
        {
            if (_soundSync.IsRunning || !Same(_soundSync.ProfilePath, path)) return Attention(SoundSync, "The profile path changed or SoundSync started.");
            string backup = _backups.CreateBackup(SoundSync, path);
            if (string.IsNullOrEmpty(backup)) return BackupFailed(SoundSync);
            if (_soundSync.IsRunning) return Attention(SoundSync, "SoundSync started while its backup was being saved.", backup);
            return _soundSync.ConfigureCableOutput(cable.Id) && _soundSync.IsCableOutputConfigured(cable.Id)
                ? Verified(SoundSync, "Verified the saved VB-CABLE output. Open SoundSync and connect its audio capture.", backup)
                : Unverified(SoundSync, "Could not verify the saved VB-CABLE output.", backup);
        });
    }

    private VoicemeeterRoutingState WaitForRouting(Func<VoicemeeterRoutingState, bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        var state = _voicemeeter.GetLiveRoutingState();
        while (!condition(state) && watch.Elapsed < timeout)
        {
            Thread.Sleep(100);
            state = _voicemeeter.GetLiveRoutingState();
        }

        return state;
    }

    private static bool ApolloMatches(ApolloConfigModel config, AudioEndpointModel vaio) =>
        config.IsReadable && Same(config.Gamepad, "ds4") && (Same(config.VirtualSink, vaio.FriendlyName) || Same(config.VirtualSink, vaio.Id));

    private static bool CanReadRouting(VoicemeeterRoutingState state) =>
        state.IsConnected && state.VoicemeeterType == 2 && state.HasReferenceParameters && state.HasOutputDeviceNames;

    private static bool RoutingMatches(VoicemeeterRoutingState state, AudioEndpointModel cable) =>
        CanReadRouting(state) && state.IsProperlyConfigured && !string.IsNullOrWhiteSpace(state.PhysicalOutA1Device) &&
        SameDeviceName(state.PhysicalOutA2Device, cable.FriendlyName);

    private static bool SameDeviceName(string actual, string expected) =>
        Same(actual, expected) || Same(actual, $"WDM: {expected}");

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static AudioEndpointModel? SelectEndpoint(IEnumerable<AudioEndpointModel> endpoints, AudioEndpointType type, string preferredName)
    {
        var candidates = endpoints.Where(endpoint => endpoint.EndpointType == type).ToArray();
        return SelectUnique(candidates) ?? SelectUnique(candidates.Where(endpoint =>
            endpoint.FriendlyName.StartsWith(preferredName, StringComparison.OrdinalIgnoreCase)));
    }

    private static AudioEndpointModel? SelectUnique(IEnumerable<AudioEndpointModel> endpoints)
    {
        var candidates = endpoints.Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static (SetupRepairItem, SetupRepairAction) Planned(SetupRepairTarget target, string detail, Func<SetupRepairItem> action) =>
        (new SetupRepairItem(target.Component, SetupRepairState.Planned, detail), new SetupRepairAction(target, action));

    private static SetupRepairItem Configured(string component, string detail) => new(component, SetupRepairState.Configured, detail);
    private static SetupRepairItem Attention(string component, string detail, string backups = "") => new(component, SetupRepairState.NeedsAttention, detail, backups);
    private static SetupRepairItem Verified(string component, string detail, string backups = "") => new(component, SetupRepairState.Verified, detail, backups);
    private static SetupRepairItem Unverified(string component, string detail, string backups) => new(component, SetupRepairState.Unverified, detail, backups);
    private static SetupRepairItem BackupFailed(string component, string backups = "") =>
        Attention(component, "A required backup could not be saved. No settings were written for this repair.", backups);
}
