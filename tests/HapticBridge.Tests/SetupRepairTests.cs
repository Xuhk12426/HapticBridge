using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using HapticBridge.Core.Models;
using HapticBridge.Integrations.Apollo;
using HapticBridge.Integrations.DS4Windows;
using HapticBridge.Integrations.SoundSync;
using HapticBridge.Setup.Prerequisites;
using HapticBridge.Setup.Backups;
using HapticBridge.Setup.Engine;
using HapticBridge.Setup.HidHide;
using HapticBridge.UI;
using HapticBridge.UI.ViewModels;
using HapticBridge.UI.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HapticBridge.Tests;

[TestClass]
public sealed class SetupRepairTests
{
    private const string CableGuid = "{d2cdcc2f-4cc1-4b89-9c36-b986130c7f52}";
    private const string CableId = "{0.0.0.00000000}." + CableGuid;
    private const string CableName = "CABLE In 16 Ch (VB-Audio Virtual Cable)";
    private const string VaioName = "Voicemeeter Input (VB-Audio Voicemeeter VAIO)";
    private const string HidHideApplication = @"\Device\HarddiskVolume3\HapticBridge\HapticBridge.exe";
    private const string OtherHidHideApplication = @"\Device\HarddiskVolume3\Tools\DS4Windows.exe";

    [TestMethod]
    public async Task OneApplyRepairsFourComponentsAfterReadOnlyInspection()
    {
        string root = NewFixtureDirectory();
        string apolloPath = Path.Combine(root, "sunshine.conf");
        const string originalApollo = "# Keep this note\r\ngamepad = x360  # input\r\nencoder = nvenc\r\n";
        File.WriteAllText(apolloPath, originalApollo);

        string ds4Profiles = Directory.CreateDirectory(Path.Combine(root, "Profiles")).FullName;
        File.WriteAllText(Path.Combine(root, "Profiles.xml"), "<Settings><UseMoonlight>false</UseMoonlight><Custom>keep</Custom></Settings>");
        File.WriteAllText(Path.Combine(ds4Profiles, "Default.xml"), "<Profile><OutputContDevice>X360</OutputContDevice></Profile>");

        string soundSyncPath = Path.Combine(root, "settings_profile.json");
        File.WriteAllText(soundSyncPath, $$"""[{"DeviceId":"{{CableGuid}}","IsSelected":false,"Volume":0.4,"Delay":12}]""");
        var soundSync = new FixtureSoundSync(soundSyncPath);
        var voicemeeter = CreateVoicemeeter();
        voicemeeter.OnConfigureReferenceRouting = (a1, a2) =>
        {
            Assert.IsNull(a1, "An existing physical output must be preserved.");
            Assert.AreEqual(CableName, a2);
            SetMatchingRouting(voicemeeter.LiveState);
        };
        string backupsPath = Path.Combine(root, "backups");
        var engine = new SetupRepairEngine(new FixtureApollo(apolloPath), new FixtureDs4Windows(root),
            voicemeeter, soundSync, CreateAudio(), new ConfigurationBackupService(backupsPath));
        var viewModel = new SetupRepairViewModel(engine);

        await viewModel.CheckAsync();
        Assert.AreEqual("4 repairs available", viewModel.Summary);
        Assert.IsTrue(viewModel.ApplyCommand.CanExecute(null));
        Assert.AreEqual(originalApollo, File.ReadAllText(apolloPath));
        Assert.IsFalse(Directory.Exists(backupsPath));
        Assert.AreEqual(0, voicemeeter.BackupRequests);

        await viewModel.ApplyAsync();

        Assert.HasCount(4, viewModel.Items.Where(item => item.State == SetupRepairState.Verified).ToArray());
        Assert.AreEqual(4, Directory.GetFiles(backupsPath, "*.bak", SearchOption.AllDirectories).Length);
        Assert.HasCount(1, soundSync.ReadProfileDevices());
        Assert.IsTrue(soundSync.IsCableOutputConfigured(CableId));
        Assert.AreEqual(12.0, soundSync.ReadProfileDevices()[0].Delay);
        StringAssert.Contains(File.ReadAllText(apolloPath), "gamepad = ds4  # input");
        StringAssert.Contains(File.ReadAllText(apolloPath), "# Keep this note\r\n");
        StringAssert.Contains(File.ReadAllText(apolloPath), "encoder = nvenc");
        Assert.IsFalse(viewModel.ApplyCommand.CanExecute(null));

        await viewModel.CheckAsync();
        Assert.IsFalse(viewModel.ApplyCommand.CanExecute(null), "A second scan must avoid rewriting a configured system.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HidHideRepairChangesOnlyThisExecutableAndKeepsTheOriginalList(bool inverse)
    {
        string root = NewFixtureDirectory();
        var session = new HidHideFixture
        {
            IsInverse = inverse,
            Applications = inverse ? [OtherHidHideApplication, HidHideApplication] : [OtherHidHideApplication]
        };
        string[] original = session.Applications;
        var backups = new ConfigurationBackupService(root);
        var hidHide = CreateHidHide(backups, session);
        var voicemeeter = CreateVoicemeeter();
        var engine = new SetupRepairEngine(new FixtureApollo(), new FixtureDs4Windows(), voicemeeter,
            new FixtureSoundSync(), CreateAudio(), backups, hidHide);

        var plan = engine.Inspect();
        Assert.IsTrue(plan.HasHidHideRepair);
        Assert.AreEqual(0, session.Writes);
        Assert.IsEmpty(Directory.GetFiles(root));

        var item = engine.Apply(plan, onlyHidHide: true).Single(row => row.Component == "HidHide");
        Assert.AreEqual(SetupRepairState.Verified, item.State);
        Assert.AreEqual(1, session.Writes);
        Assert.AreEqual(0, voicemeeter.ReferenceRoutingRequests, "The dedicated action must leave other repairs pending.");
        CollectionAssert.AreEqual(inverse ? new[] { OtherHidHideApplication } : new[] { OtherHidHideApplication, HidHideApplication },
            session.Applications);
        using var saved = JsonDocument.Parse(File.ReadAllText(item.BackupPaths));
        CollectionAssert.AreEqual(original, saved.RootElement.GetProperty("Applications").Deserialize<string[]>());
        Assert.IsFalse(engine.Inspect().HasHidHideRepair);
        engine.Apply(plan, onlyHidHide: true);
        Assert.AreEqual(1, session.Writes, "Reapplying an approved repair must be idempotent.");
    }

    [TestMethod]
    public void HidHideRefusesAChangedModeAndRequiresABackupBeforeWriting()
    {
        string root = NewFixtureDirectory();
        var session = new HidHideFixture { IsInverse = true, Applications = [HidHideApplication] };
        var hidHide = CreateHidHide(new ConfigurationBackupService(root), session);

        Assert.ThrowsExactly<IOException>(() => hidHide.AllowAccess(approvedInverse: false));
        Assert.AreEqual(0, session.Writes);

        string invalidBackupRoot = Path.Combine(root, "file-at-backup-root");
        File.WriteAllText(invalidBackupRoot, "");
        hidHide = CreateHidHide(new ConfigurationBackupService(invalidBackupRoot), session);
        Assert.ThrowsExactly<IOException>(() => hidHide.AllowAccess(approvedInverse: true));
        Assert.AreEqual(0, session.Writes);
    }

    [TestMethod]
    public void HidHideReadbackMustRetainOtherApplications()
    {
        var session = new HidHideFixture { Applications = [OtherHidHideApplication], DropOtherEntriesOnWrite = true };
        var hidHide = CreateHidHide(new ConfigurationBackupService(NewFixtureDirectory()), session);

        var result = hidHide.AllowAccess(approvedInverse: false);
        Assert.IsFalse(result.Verified);
        Assert.IsTrue(File.Exists(result.BackupPath));
    }

    private static HidHideAccessService CreateHidHide(ConfigurationBackupService backups, HidHideFixture session) =>
        new(backups, @"C:\HapticBridge\HapticBridge.exe", () => session, () => HidHideApplication);

    private sealed class HidHideFixture : IHidHideSession
    {
        public bool IsHidingEnabled => true;
        public bool IsInverse { get; init; }
        public string[] Applications { get; set; } = [];
        public bool DropOtherEntriesOnWrite { get; init; }
        public int Writes { get; private set; }
        public string[] ReadApplications() => Applications;
        public void WriteApplications(string[] applications)
        {
            Writes++;
            Applications = DropOtherEntriesOnWrite ? [HidHideApplication] : applications;
        }
        public void Dispose() { }
    }

    [TestMethod]
    public void FailedBackupPreventsRoutingWrites()
    {
        var voicemeeter = CreateVoicemeeter();
        voicemeeter.BackupSucceeds = false;
        var engine = CreateEngine(CreateAudio(), voicemeeter);

        var result = engine.Apply(engine.Inspect()).Single(item => item.Component == "Voicemeeter Banana");

        Assert.AreEqual(SetupRepairState.NeedsAttention, result.State);
        StringAssert.Contains(result.Detail, "backup could not be saved");
        Assert.AreEqual(1, voicemeeter.BackupRequests);
        Assert.AreEqual(0, voicemeeter.ReferenceRoutingRequests);
    }

    [TestMethod]
    public void ChangedEndpointRequiresAnotherReviewAndDoesNotWrite()
    {
        var voicemeeter = CreateVoicemeeter();
        var audio = CreateAudio();
        var engine = CreateEngine(audio, voicemeeter);
        var plan = engine.Inspect();
        Assert.AreEqual(1, plan.ChangeCount);

        audio.RenderEndpoints.Single(endpoint => endpoint.EndpointType == AudioEndpointType.VirtualCable).Id = "replacement-cable";
        var result = engine.Apply(plan).Single(item => item.Component == "Voicemeeter Banana");

        Assert.AreEqual(SetupRepairState.NeedsAttention, result.State);
        StringAssert.Contains(result.Detail, "target or a prerequisite changed");
        Assert.AreEqual(0, voicemeeter.BackupRequests);
        Assert.AreEqual(0, voicemeeter.ReferenceRoutingRequests);
    }

    [TestMethod]
    public void StoppedBananaStartsOnlyDuringApplyAndKeepsACorrectRoute()
    {
        var voicemeeter = CreateVoicemeeter();
        voicemeeter.IsRunning = false;
        voicemeeter.CanLaunchBanana = true;
        int launches = 0;
        voicemeeter.OnLaunch = () =>
        {
            launches++;
            voicemeeter.IsRunning = true;
            SetMatchingRouting(voicemeeter.LiveState);
        };
        var engine = CreateEngine(CreateAudio(), voicemeeter);

        var plan = engine.Inspect();
        Assert.AreEqual(1, plan.ChangeCount);
        Assert.AreEqual(0, launches);
        var result = engine.Apply(plan).Single(item => item.Component == "Voicemeeter Banana");

        Assert.AreEqual(1, launches);
        Assert.AreEqual(SetupRepairState.Verified, result.State);
        Assert.AreEqual(0, voicemeeter.ReferenceRoutingRequests);
    }

    [TestMethod]
    public void AmbiguousCableAndRunningProfileEditorBlockTheirRepairs()
    {
        var audio = CreateAudio();
        audio.RenderEndpoints.Add(new AudioEndpointModel
            { Id = "second-cable", FriendlyName = CableName, IsActive = true, EndpointType = AudioEndpointType.VirtualCable });
        var voicemeeter = CreateVoicemeeter();
        var plan = CreateEngine(audio, voicemeeter).Inspect();
        Assert.AreEqual(0, plan.ChangeCount);

        string root = NewFixtureDirectory();
        Directory.CreateDirectory(Path.Combine(root, "Profiles"));
        File.WriteAllText(Path.Combine(root, "Profiles.xml"), "<Settings/>");
        File.WriteAllText(Path.Combine(root, "Profiles", "Default.xml"), "<Profile/>");
        var ds4 = new FixtureDs4Windows(root, running: true);
        var engine = new SetupRepairEngine(new FixtureApollo(), ds4, voicemeeter,
            new FixtureSoundSync(), audio, new ConfigurationBackupService(Path.Combine(root, "backups")));
        var ds4Item = engine.Inspect().Items.Single(item => item.Component == "DS4Windows");
        Assert.AreEqual(SetupRepairState.NeedsAttention, ds4Item.State);
        StringAssert.Contains(ds4Item.Detail, "Close DS4Windows");
    }

    [STATestMethod]
    public void WindowsLoadWithTheirBindings()
    {
        var app = new App();
        app.InitializeComponent();
        var model = new SetupRepairViewModel(CreateEngine(CreateAudio(), CreateVoicemeeter()));
        var window = new SetupRepairWindow(model);
        try
        {
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            var button = window.FindName("ApplyRepairsButton") as Button;
            Assert.IsNotNull(button);
            Assert.AreEqual("Apply repairs", button.Content);
            Assert.AreSame(model.ApplyCommand, button.Command);
            Assert.IsFalse(button.IsEnabled, "Apply must wait for read-only inspection.");
        }
        finally
        {
            window.Close();
        }

        var installer = new PrerequisiteInstaller(_ => (PrerequisiteState.Missing, null),
            (_, _, _, _) => throw new InvalidOperationException("The window must never install on load."));
        var wizard = new PrerequisitesViewModel(installer, SetupRoute.ExternalTools, new FixtureSoundSync());
        var prerequisites = new PrerequisitesWindow(wizard);
        try
        {
            prerequisites.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            var install = prerequisites.FindName("InstallMissingButton") as Button;
            Assert.IsNotNull(install);
            Assert.AreEqual("Set up External tools", prerequisites.Title);
            Assert.AreSame(wizard.InstallCommand, install.Command);
            Assert.IsFalse(install.IsEnabled, "Install must wait until the prerequisite list is displayed.");
        }
        finally
        {
            prerequisites.Close();
        }

        var controller = new ControllerSessionViewModel(() => false, () => false, (_, _) => throw new InvalidOperationException());
        var tests = new NativeTestViewModel(CreateAudio(), () => false, controller);
        var testWindow = new NativeTestWindow(tests);
        try
        {
            testWindow.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            var startAudio = testWindow.FindName("StartAudioButton") as Button;
            var startController = testWindow.FindName("StartControllerButton") as Button;
            Assert.IsNotNull(startAudio);
            Assert.IsNotNull(startController);
            Assert.AreEqual("Enable DualSense haptics", startController.Content);
            Assert.AreSame(tests.StartAudioCommand, startAudio.Command);
            Assert.AreSame(controller.StartCommand, startController.Command);
            Assert.IsFalse(startAudio.IsEnabled, "The audio test needs selected endpoints.");
        }
        finally
        {
            testWindow.Close();
        }

        var apollo = new ApolloSettingsViewModel(
            () => Task.FromResult(new ApolloHostStatus(new Uri("https://localhost:47990/"), ApolloAudioMode.Available, true)), _ => { });
        var host = new ApolloHostStatus(new Uri("https://localhost:47990/"), ApolloAudioMode.Available, true);
        using (var client = new ApolloAdminClient(new System.Net.Http.HttpClient()))
        {
            var apolloWindow = new ApolloManagementWindow(client, host, ApolloManagementPage.Devices,
                () => Task.FromResult(true));
            try
            {
                Assert.IsInstanceOfType<PasswordBox>(apolloWindow.FindName("Password"));
                Assert.IsInstanceOfType<ListBox>(apolloWindow.FindName("DeviceList"));
            }
            finally { apolloWindow.Close(); }
        }
        var dashboard = ReadinessTests.CreateDashboard(CreateAudio(), CreateVoicemeeter(), controller: controller, apollo: apollo);
        var mainWindow = new MainWindow(dashboard);
        try
        {
            mainWindow.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            var picker = mainWindow.FindName("ListeningDevicePicker") as ComboBox;
            var volume = mainWindow.FindName("ListeningVolumeSlider") as Slider;
            var hidHide = mainWindow.FindName("DashboardHidHideButton") as Button;
            var hapticsTab = mainWindow.FindName("NativePathButton") as Button;
            var nativeSetup = mainWindow.FindName("NativePrerequisitesButton") as Button;
            var externalSetup = mainWindow.FindName("ExternalPrerequisitesButton") as Button;
            var apolloTab = mainWindow.FindName("ApolloTabButton") as Button;
            var apolloMode = mainWindow.FindName("ApolloAudioModeButton") as Button;
            var apolloWebUi = mainWindow.FindName("ApolloWebUiButton") as Button;
            var connect = mainWindow.FindName("ConnectDualSenseButton") as Button;
            var disconnect = mainWindow.FindName("DisconnectDualSenseButton") as Button;
            var pulseTrail = mainWindow.FindName("StartPulseTrailButton") as Button;
            var beat = mainWindow.FindName("PlayDualSenseBeatButton") as Button;
            Assert.IsNotNull(picker);
            Assert.IsNotNull(volume);
            Assert.IsNotNull(hidHide);
            Assert.IsNotNull(hapticsTab);
            Assert.IsNotNull(nativeSetup);
            Assert.IsNotNull(externalSetup);
            Assert.IsNotNull(apolloTab);
            Assert.IsNotNull(apolloMode);
            Assert.IsNotNull(apolloWebUi);
            Assert.IsNotNull(connect);
            Assert.IsNotNull(disconnect);
            Assert.IsNotNull(pulseTrail);
            Assert.IsNotNull(beat);
            Assert.AreEqual("DualSense Haptics", hapticsTab.Content);
            Assert.AreSame(dashboard.ShowNativeCommand, hapticsTab.Command);
            Assert.AreSame(dashboard.OpenPrerequisitesCommand, nativeSetup.Command);
            Assert.AreSame(dashboard.OpenPrerequisitesCommand, externalSetup.Command);
            SetupRoute? route = null;
            dashboard.RequestOpenPrerequisites += selected => route = selected;
            dashboard.OpenPrerequisitesCommand.Execute(null);
            Assert.AreEqual(SetupRoute.DualSenseHaptics, route, "The Apollo page checks the primary route.");
            Assert.AreEqual("Enable DualSense haptics", connect.Content);
            Assert.AreSame(controller.StartCommand, connect.Command);
            Assert.AreEqual("Disable haptics", disconnect.Content);
            Assert.AreSame(controller.StopCommand, disconnect.Command);
            StringAssert.Contains(controller.Status, "virtual DualSense");
            Assert.AreSame(dashboard.Testing.StartChallengeCommand, pulseTrail.Command);
            Assert.AreSame(dashboard.Testing.PlayDualSenseCommand, beat.Command);
            Assert.IsFalse(pulseTrail.IsEnabled, "Pulse Trail requires a virtual DualSense session.");
            Assert.IsFalse(beat.IsEnabled, "The beat requires a virtual DualSense session.");
            Assert.AreEqual(10, dashboard.Testing.CueCount);
            CollectionAssert.AreEqual(new[] { "A → × Cross", "B → ○ Circle", "X → □ Square", "Y → △ Triangle" },
                dashboard.Testing.FaceButtonGuide.ToArray());
            Assert.AreSame(dashboard.RepairHidHideCommand, hidHide.Command);
            Assert.AreSame(dashboard.PhysicalAudioDevices, picker.ItemsSource);
            Assert.AreEqual(nameof(DashboardViewModel.SelectedAudioDevice),
                picker.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path.Path);
            var volumeBinding = volume.GetBindingExpression(Slider.ValueProperty)?.ParentBinding;
            Assert.AreEqual(nameof(DashboardViewModel.ListeningVolume), volumeBinding?.Path.Path);
            Assert.AreEqual(System.Windows.Data.BindingMode.TwoWay, volumeBinding?.Mode);
            Assert.IsFalse(volume.IsEnabled, "A confirmed output is required.");
            Assert.AreSame(dashboard.ShowApolloCommand, apolloTab.Command);
            Assert.AreSame(apollo.OpenModeSettingsCommand, apolloMode.Command);
            Assert.AreSame(apollo.OpenWebUiCommand, apolloWebUi.Command);
            Assert.IsTrue(dashboard.IsApollo);
            Assert.IsFalse(apolloTab.Command.CanExecute(null), "Apollo is the initial tab.");
            Assert.IsTrue(dashboard.UsesExternalTools, "Opening Apollo settings must preserve the audio route.");
        }
        finally
        {
            mainWindow.Close();
        }
    }

    private static string NewFixtureDirectory() => Directory.CreateTempSubdirectory("HapticBridge-setup-").FullName;

    private static SetupRepairEngine CreateEngine(MockAudioEndpointService audio, MockVoicemeeterManager voicemeeter) =>
        new(new FixtureApollo(), new FixtureDs4Windows(), voicemeeter, new FixtureSoundSync(),
            audio, new ConfigurationBackupService(Path.Combine(NewFixtureDirectory(), "backups")));

    private static MockVoicemeeterManager CreateVoicemeeter() => new()
    {
        IsRunning = true,
        LiveState = new VoicemeeterRoutingState
        {
            IsConnected = true, VoicemeeterType = 2, HasReferenceParameters = true, HasOutputDeviceNames = true,
            PhysicalOutA1Device = "Existing ASIO"
        }
    };

    private static void SetMatchingRouting(VoicemeeterRoutingState routing)
    {
        routing.StripVaioA1 = true;
        routing.StripVaioA2 = true;
        routing.BusA2Composite = true;
        routing.PatchCompositeCh1 = 11;
        routing.PatchCompositeCh2 = 12;
        routing.PhysicalOutA2Device = CableName;
    }

    private static MockAudioEndpointService CreateAudio()
    {
        var audio = new MockAudioEndpointService();
        audio.RenderEndpoints.AddRange([
            new AudioEndpointModel { Id = CableId, FriendlyName = CableName, IsActive = true, EndpointType = AudioEndpointType.VirtualCable },
            new AudioEndpointModel { Id = "vaio", FriendlyName = VaioName, IsActive = true, EndpointType = AudioEndpointType.VoicemeeterVAIO },
            new AudioEndpointModel { Id = "speakers", FriendlyName = "Speakers", IsActive = true, IsDefault = true }
        ]);
        return audio;
    }

    private sealed class FixtureApollo(string path = "") : ApolloIntegration
    {
        public override string ConfigurationPath => path;
        public override bool IsInstalled => path.Length > 0;
        public override bool IsRunning => IsInstalled;
    }

    private sealed class FixtureDs4Windows(string path = "", bool running = false) : DS4WindowsIntegration
    {
        public override string ConfigurationDirectory => path;
        public override bool IsInstalled => path.Length > 0;
        public override bool IsRunning => running;
        public override bool IsViiperRunning => false;
    }

    private sealed class FixtureSoundSync(string path = "") : SoundSyncIntegration
    {
        public override string ProfilePath => path;
        public override bool IsInstalled => path.Length > 0;
        public override bool IsRunning => false;
    }
}
