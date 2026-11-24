using HapticBridge.Audio.Wasapi;
using HapticBridge.Core.Models;
using HapticBridge.Core.Services;
using HapticBridge.Integrations.Apollo;
using HapticBridge.Integrations.Components;
using HapticBridge.Integrations.DS4Windows;
using HapticBridge.Integrations.SoundSync;
using HapticBridge.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HapticBridge.Tests;

[TestClass]
public class ReadinessTests
{
    [TestMethod]
    public void StoppedToolRequiresAnExecutableBeforeReportingInstalled()
    {
        var item = new SystemComponentItem { Name = "SoundSync" };
        string file = System.IO.Path.GetTempFileName();
        try
        {
            Assert.AreEqual(ComponentLifecycleState.InstalledDormant,
                ComponentDetectionService.DescribeStoppedTool(item, file).LifecycleState);
            Assert.AreEqual(ComponentLifecycleState.Unknown,
                ComponentDetectionService.DescribeStoppedTool(item, file + ".missing").LifecycleState);
            Assert.AreEqual(ComponentLifecycleState.Unknown,
                ComponentDetectionService.DescribeStoppedTool(item, "DS4Windows.exe").LifecycleState);
        }
        finally { System.IO.File.Delete(file); }
    }

    [TestMethod]
    public async Task TestingTabLeavesTheNativeSessionRunning()
    {
        var controller = new ControllerSessionViewModel(() => true, () => false, async (_, stop) =>
        {
            await Task.Delay(Timeout.Infinite, stop);
            return new(0, 0, "Stopped.");
        });
        using var playback = new WasapiStereoPlayback();
        var dashboard = new DashboardViewModel(new MockAudioEndpointService(), new MockVoicemeeterManager(),
            new ComponentSnapshot(), new ControllerSnapshot(), controllerSession: controller, stereoPlayback: playback);

        var session = controller.StartAsync();
        try
        {
            await dashboard.SwitchPageAsync(DashboardViewModel.DashboardPage.Testing);
            Assert.IsTrue(dashboard.IsTesting);
            Assert.IsTrue(dashboard.UsesNativeAudio);
            Assert.IsTrue(controller.IsRunning);
            Assert.IsFalse(dashboard.Testing.PlayDualSenseCommand.CanExecute(null));
            Assert.IsFalse(dashboard.Testing.StartChallengeCommand.CanExecute(null));
            Assert.IsFalse(dashboard.ShowExternalToolsCommand.CanExecute(null));
        }
        finally
        {
            await controller.StopAsync();
            await session;
        }
    }

    [TestMethod]
    public async Task ControllerTestsFollowActualInputAndBeatAlsoNeedsSpeakers()
    {
        var started = new TaskCompletionSource<IProgress<HapticBridge.Integrations.VIIPER.DualSenseTestInput?>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new ControllerSessionViewModel(() => true, () => false, null, async (_, stop, input) =>
        {
            started.SetResult(input);
            await Task.Delay(Timeout.Infinite, stop);
            return new(0, 0, "Stopped.");
        });
        AudioEndpointModel? speaker = null;
        var testing = new GameFreeTestingViewModel(new MockAudioEndpointService(), new ControllerSnapshot(),
            controller, () => speaker);
        var session = controller.StartAsync();
        try
        {
            var input = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (bool connected in new[] { true, false, true })
            {
                var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(controller.HasInput)) observed.TrySetResult();
                }
                controller.PropertyChanged += Changed;
                try
                {
                    input.Report(connected ? new(0, 0, 0) : null);
                    await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { controller.PropertyChanged -= Changed; }
                Assert.AreEqual(connected, testing.StartChallengeCommand.CanExecute(null));
                Assert.IsFalse(testing.PlayDualSenseCommand.CanExecute(null));
            }
            speaker = new AudioEndpointModel { Id = "speakers" };
            Assert.IsTrue(testing.PlayDualSenseCommand.CanExecute(null));
        }
        finally { await controller.StopAsync(); await session; }
        Assert.IsFalse(testing.StartChallengeCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task NativeIsTheDefaultAndKeepsItsOutputWhenSwitchingFromExternalTools()
    {
        var audio = new MockAudioEndpointService();
        var speakers = new AudioEndpointModel { Id = "speakers", FriendlyName = "Speakers", IsDefault = true, IsMuted = false };
        var headset = new AudioEndpointModel { Id = "headset", FriendlyName = "Headset", IsMuted = false };
        audio.RenderEndpoints.AddRange([speakers, headset]);
        var voicemeeter = new MockVoicemeeterManager { ListeningOutput = ("Headset", 72) };
        using var playback = new WasapiStereoPlayback();
        var dashboard = new DashboardViewModel(audio, voicemeeter, new ComponentSnapshot(),
            new ControllerSnapshot(), stereoPlayback: playback);

        await dashboard.RefreshAllAsync();
        Assert.IsTrue(dashboard.IsNative);
        Assert.IsFalse(dashboard.SetupRepairCommand.CanExecute(null));
        Assert.AreSame(speakers, dashboard.SelectedAudioDevice);
        Assert.AreEqual(100, dashboard.ListeningVolume);
        dashboard.ListeningVolume = 39;
        Assert.IsEmpty(voicemeeter.RequestedListeningVolumes);

        await dashboard.SwitchPathAsync(externalToolsOnly: true);
        Assert.IsTrue(dashboard.UsesExternalTools);
        Assert.IsTrue(dashboard.SetupRepairCommand.CanExecute(null));
        Assert.AreSame(headset, dashboard.SelectedAudioDevice);
        Assert.AreEqual(72, dashboard.ListeningVolume);
        dashboard.ListeningVolume = 48;
        CollectionAssert.AreEqual(new[] { ("Headset", 48) }, voicemeeter.RequestedListeningVolumes);
        CollectionAssert.AreEqual(new[] { ("speakers", 39) }, audio.PlaybackVolumeRequests);

        voicemeeter.ListeningOutput = null;
        await dashboard.SwitchPathAsync(externalToolsOnly: false);
        Assert.AreSame(speakers, dashboard.SelectedAudioDevice);
        Assert.AreEqual(39, dashboard.ListeningVolume);
        await dashboard.SwitchPathAsync(externalToolsOnly: true);
        Assert.IsFalse(dashboard.CanAdjustListeningVolume);
        StringAssert.Contains(dashboard.AudioOutputStatus, "Voicemeeter");
    }

    [TestMethod]
    public async Task NativeVolumeShowsWindowsMuteAndCanRestoreTheSelectedOutput()
    {
        var audio = new MockAudioEndpointService();
        var speakers = new AudioEndpointModel
        {
            Id = "speakers", FriendlyName = "Speakers", IsDefault = true, VolumeLevel = 0.2f, IsMuted = true
        };
        audio.RenderEndpoints.Add(speakers);
        using var playback = new WasapiStereoPlayback();
        var dashboard = new DashboardViewModel(audio, new MockVoicemeeterManager(), new ComponentSnapshot(),
            new ControllerSnapshot(), stereoPlayback: playback);

        await dashboard.RefreshAllAsync();
        Assert.IsTrue(dashboard.CanAdjustListeningVolume);
        Assert.AreEqual(0, dashboard.ListeningVolume);
        StringAssert.Contains(dashboard.AudioOutputStatus, "Windows has muted");
        Assert.IsEmpty(audio.PlaybackVolumeRequests);

        dashboard.ListeningVolume = 30;
        CollectionAssert.AreEqual(new[] { ("speakers", 30) }, audio.PlaybackVolumeRequests);
        Assert.AreEqual(false, speakers.IsMuted);
        Assert.AreEqual(30, dashboard.ListeningVolume);

        // The endpoint service returns new snapshots on subsequent Windows reads.
        audio.RenderEndpoints[0] = new AudioEndpointModel
        {
            Id = "speakers", FriendlyName = "Speakers", IsDefault = true, VolumeLevel = 0.12f, IsMuted = false
        };
        await dashboard.RefreshAllAsync();
        Assert.AreEqual(12, dashboard.ListeningVolume);
        audio.AcceptPlaybackVolume = false;
        dashboard.ListeningVolume = 50;
        Assert.AreEqual(12, dashboard.ListeningVolume);
        Assert.IsFalse(dashboard.CanAdjustListeningVolume);
        await dashboard.RefreshAllAsync();
        Assert.IsTrue(dashboard.CanAdjustListeningVolume);
        Assert.AreEqual(12, dashboard.ListeningVolume);
    }

    [TestMethod]
    public async Task FailedDeviceReadDisablesStaleAudioControlsUntilARefreshSucceeds()
    {
        var audio = new MockAudioEndpointService();
        audio.RenderEndpoints.Add(new AudioEndpointModel { Id = "speakers", IsDefault = true, IsMuted = false });
        var controllers = new ControllerSnapshot();
        using var playback = new WasapiStereoPlayback();
        var dashboard = new DashboardViewModel(audio, new MockVoicemeeterManager(), new ComponentSnapshot(),
            controllers, stereoPlayback: playback);
        await dashboard.RefreshAllAsync();
        Assert.IsTrue(dashboard.CanAdjustListeningVolume);
        controllers.FailInspection = true;
        await dashboard.RefreshAllAsync();
        Assert.IsFalse(dashboard.CanSelectAudioOutput);
        Assert.IsFalse(dashboard.Testing.PlaySpeakersCommand.CanExecute(null));
        dashboard.ListeningVolume = 77;
        Assert.IsEmpty(audio.PlaybackVolumeRequests);
        StringAssert.Contains(dashboard.AudioOutputStatus, "Refresh");
        controllers.FailInspection = false;
        await dashboard.RefreshAllAsync();
        Assert.IsTrue(dashboard.CanAdjustListeningVolume);
        Assert.IsTrue(dashboard.Testing.PlaySpeakersCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task DashboardRefreshKeepsAudioSelectionWithoutWritingRouting()
    {
        var audio = new MockAudioEndpointService();
        var voicemeeter = new MockVoicemeeterManager { ListeningOutput = (string.Empty, 100) };
        var dashboard = CreateDashboard(audio, voicemeeter, new ComponentSnapshot());
        var speakers = new AudioEndpointModel { Id = "speakers", FriendlyName = "Speakers", IsActive = true };
        audio.RenderEndpoints.Add(speakers);

        // Simulate ComboBox selection notifications while its items are being refreshed.
        dashboard.PhysicalAudioDevices.CollectionChanged += (_, _) => dashboard.SelectedAudioDevice = speakers;
        await dashboard.RefreshAllAsync();
        Assert.IsEmpty(voicemeeter.RequestedPhysicalOutputs);
        Assert.IsNull(dashboard.SelectedAudioDevice);

        dashboard.SelectedAudioDevice = speakers;
        voicemeeter.ListeningOutput = ("Speakers", 68);
        await Task.Delay(600);
        audio.RenderEndpoints.Add(new AudioEndpointModel { Id = "headphones", FriendlyName = "Headphones" });
        await dashboard.RefreshAllAsync();
        Assert.AreEqual(speakers.Id, dashboard.SelectedAudioDevice?.Id);
        CollectionAssert.AreEqual(new[] { "Speakers" }, voicemeeter.RequestedPhysicalOutputs);
        Assert.AreEqual(0, voicemeeter.ReferenceRoutingRequests);

        audio.RenderEndpoints.Remove(speakers);
        await dashboard.RefreshAllAsync();
        Assert.IsNull(dashboard.SelectedAudioDevice);
        Assert.HasCount(1, voicemeeter.RequestedPhysicalOutputs);
    }

    [TestMethod]
    public async Task DashboardLoadsMixerSelectionAndControlsOnlyConfirmedListeningOutput()
    {
        var audio = new MockAudioEndpointService();
        var defaultSpeakers = new AudioEndpointModel { Id = "speakers", FriendlyName = "Speakers", IsDefault = true };
        var headphones = new AudioEndpointModel { Id = "headphones", FriendlyName = "Headphones" };
        audio.RenderEndpoints.AddRange([defaultSpeakers, headphones]);
        var voicemeeter = new MockVoicemeeterManager { ListeningOutput = ("Headphones", 72) };
        var dashboard = CreateDashboard(audio, voicemeeter, new ComponentSnapshot());

        await dashboard.RefreshAllAsync();
        Assert.AreSame(headphones, dashboard.SelectedAudioDevice);
        Assert.IsTrue(dashboard.CanAdjustListeningVolume);
        Assert.AreEqual("72%", dashboard.ListeningVolumeText);
        Assert.IsEmpty(voicemeeter.RequestedPhysicalOutputs);
        Assert.IsEmpty(voicemeeter.RequestedListeningVolumes);

        dashboard.ListeningVolume = 40;
        dashboard.ListeningVolume = 0;
        await dashboard.RefreshAllAsync();
        CollectionAssert.AreEqual(new[] { ("Headphones", 40), ("Headphones", 0) }, voicemeeter.RequestedListeningVolumes);
        Assert.AreEqual("0%", dashboard.ListeningVolumeText, "The previous mixer snapshot must not reset the slider.");

        dashboard.SelectedAudioDevice = defaultSpeakers;
        dashboard.ListeningVolume = 60;
        Assert.IsFalse(dashboard.CanAdjustListeningVolume);
        Assert.HasCount(2, voicemeeter.RequestedListeningVolumes);
        voicemeeter.ListeningOutput = ("Speakers", 34);
        await Task.Delay(600);
        await dashboard.RefreshAllAsync();
        Assert.AreSame(defaultSpeakers, dashboard.SelectedAudioDevice);
        Assert.AreEqual("34%", dashboard.ListeningVolumeText);

        voicemeeter.ListeningOutput = ("Speakers", 16);
        await dashboard.RefreshAllAsync();
        Assert.AreEqual("16%", dashboard.ListeningVolumeText);
        Assert.HasCount(2, voicemeeter.RequestedListeningVolumes);
        Assert.AreEqual(0, voicemeeter.ReferenceRoutingRequests);

        voicemeeter.ListeningOutput = null;
        await dashboard.RefreshAllAsync();
        Assert.IsFalse(dashboard.CanSelectAudioOutput);
        Assert.IsFalse(dashboard.CanAdjustListeningVolume);
        Assert.IsNull(dashboard.SelectedAudioDevice);
    }

    [TestMethod]
    public async Task DashboardDisablesVolumeIfVoicemeeterDoesNotConfirmSwitchOrRejectsWrite()
    {
        var audio = new MockAudioEndpointService();
        var speakers = new AudioEndpointModel { Id = "speakers", FriendlyName = "Speakers" };
        audio.RenderEndpoints.Add(speakers);
        var voicemeeter = new MockVoicemeeterManager { ListeningOutput = (string.Empty, 100) };
        var dashboard = CreateDashboard(audio, voicemeeter, new ComponentSnapshot());

        await dashboard.RefreshAllAsync();
        dashboard.SelectedAudioDevice = speakers;
        await Task.Delay(600);
        await dashboard.RefreshAllAsync();
        StringAssert.Contains(dashboard.AudioOutputStatus, "did not confirm");
        Assert.IsFalse(dashboard.CanAdjustListeningVolume);

        voicemeeter.ListeningOutput = ("Speakers", 60);
        voicemeeter.AcceptListeningVolume = false;
        await dashboard.RefreshAllAsync();
        dashboard.ListeningVolume = 80;
        Assert.AreEqual(60, dashboard.ListeningVolume);
        Assert.IsFalse(dashboard.CanAdjustListeningVolume);
        StringAssert.Contains(dashboard.AudioOutputStatus, "could not adjust");
    }

    [TestMethod]
    public async Task DashboardReportsInactiveAndUnverifiedStates()
    {
        var components = new ComponentSnapshot();
        var dashboard = CreateDashboard(new MockAudioEndpointService(), new MockVoicemeeterManager(), components);

        await dashboard.RefreshAllAsync();
        Assert.AreEqual("Haptics unverified", dashboard.SystemStatusText);

        components.Voicemeeter = new SystemComponentItem
        {
            Name = "Voicemeeter Banana",
            StatusText = "Stopped / Dormant",
            LifecycleState = ComponentLifecycleState.InstalledDormant,
            Health = ComponentHealthState.NeedsAttention
        };
        await dashboard.RefreshAllAsync();
        Assert.AreEqual("Components inactive", dashboard.SystemStatusText);
        Assert.AreEqual("Stopped / Dormant", dashboard.HapticRoutingStatus);
    }

    [TestMethod]
    public void RunningVoicemeeterWithIncompleteReadsDoesNotReportConfigured()
    {
        var voicemeeter = new MockVoicemeeterManager
        {
            IsRunning = true,
            LiveState = new VoicemeeterRoutingState { IsConnected = true }
        };
        var detector = new ComponentDetectionService(new ApolloIntegration(), new DS4WindowsIntegration(),
            voicemeeter, new MockAudioEndpointService(), new SoundSyncIntegration());

        var result = detector.DetectVoicemeeter();

        Assert.AreEqual("Routing unverified", result.StatusText);
        Assert.AreEqual(ComponentHealthState.NeedsAttention, result.Health);
    }

    internal static DashboardViewModel CreateDashboard(MockAudioEndpointService audio, MockVoicemeeterManager voicemeeter,
        IComponentDetectionService? components = null, ControllerSessionViewModel? controller = null,
        ApolloSettingsViewModel? apollo = null) =>
        new(audio, voicemeeter, components ?? new ComponentSnapshot(), new ControllerSnapshot(),
            controllerSession: controller, apolloSettings: apollo);

    private sealed class ControllerSnapshot : IControllerDetectionService
    {
        public bool FailInspection { get; set; }
        public ControllerDetectionResult DetectControllers() => FailInspection
            ? throw new InvalidOperationException("Inspection interrupted.") : new()
        {
            IsVirtualDualSenseDetected = true,
            PipelineStatus = new ControllerPipelineStatus { IsDualSenseAudioEndpointPresent = true }
        };
    }

    private sealed class ComponentSnapshot : IComponentDetectionService
    {
        public SystemComponentItem Voicemeeter { get; set; } = new()
        {
            Name = "Voicemeeter Banana",
            StatusText = "Configured",
            LifecycleState = ComponentLifecycleState.Running,
            Health = ComponentHealthState.Working
        };

        public Task<IReadOnlyList<SystemComponentItem>> DetectAllComponentsAsync() =>
            Task.FromResult<IReadOnlyList<SystemComponentItem>>([Voicemeeter]);
        public SystemComponentItem DetectVoicemeeter() => Voicemeeter;
        public SystemComponentItem DetectApollo() => throw new NotSupportedException();
        public SystemComponentItem DetectDS4Windows() => throw new NotSupportedException();
        public SystemComponentItem DetectVbCable() => throw new NotSupportedException();
        public SystemComponentItem DetectSoundSync() => throw new NotSupportedException();
    }
}
