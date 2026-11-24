using System.Windows;
using HapticBridge.Audio.Services;
using HapticBridge.Audio.Voicemeeter;
using HapticBridge.Audio.Wasapi;
using HapticBridge.Core;
using HapticBridge.Integrations.Apollo;
using HapticBridge.Integrations.Components;
using HapticBridge.Integrations.Controllers;
using HapticBridge.Integrations.DS4Windows;
using HapticBridge.Integrations.SoundSync;
using HapticBridge.Setup.Backups;
using HapticBridge.Setup.Diagnostics;
using HapticBridge.Setup.Engine;
using HapticBridge.Setup.HidHide;
using HapticBridge.Setup.Prerequisites;
using HapticBridge.UI.ViewModels;
using HapticBridge.UI.Views;

namespace HapticBridge.UI;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        if (e.Args is [ApolloDs4Setup.Command])
        {
            Shutdown(ApolloDs4Setup.RunHelper());
            return;
        }

        // 1. Initialize services
        var audioService = new WindowsAudioEndpointService();
        var stereoPlayback = new WasapiStereoPlayback();
        var voicemeeterManager = new VoicemeeterManager();
        var apolloIntegration = new ApolloIntegration();
        var ds4Integration = new DS4WindowsIntegration();
        var soundSyncIntegration = new SoundSyncIntegration();

        var controllerService = new ControllerDetectionService(audioService);
        var componentService = new ComponentDetectionService(
            apolloIntegration,
            ds4Integration,
            voicemeeterManager,
            audioService,
            soundSyncIntegration);

        var backupService = new ConfigurationBackupService();
        var setupEngine = new SetupRepairEngine(
            apolloIntegration,
            ds4Integration,
            voicemeeterManager,
            soundSyncIntegration,
            audioService,
            backupService,
            new HidHideAccessService(backupService));

        var diagnosticsService = new DiagnosticsService(
            audioService,
            voicemeeterManager,
            componentService,
            controllerService);
        var controllerSession = new ControllerSessionViewModel(apolloIntegration, ds4Integration,
            stereoPlayback.Submit, setupEngine.PrepareNativeControllerAccess, audioService.MaximizeNewHapticsEndpointAsync);
        ControllerDisconnectAlert? disconnectNotification = null;
        controllerSession.UnexpectedDisconnect += detail =>
        {
            try
            {
                disconnectNotification?.Close();
                var alert = new ControllerDisconnectAlert(detail);
                disconnectNotification = alert;
                alert.Closed += (_, _) => disconnectNotification = null;
                alert.Show();
            }
            catch (InvalidOperationException exception)
            {
                System.Diagnostics.Trace.TraceWarning("Could not show the controller disconnect notification: {0}", exception);
            }
        };

        // 2. Initialize ViewModels
        var dashboardVm = new DashboardViewModel(
            audioService,
            voicemeeterManager,
            componentService,
            controllerService,
            setupEngine,
            controllerSession,
            stereoPlayback,
            new ApolloSettingsViewModel(new ApolloHostClient(apolloIntegration)));

        dashboardVm.RequestOpenSetup += () =>
        {
            var setupWindow = new SetupRepairWindow(new SetupRepairViewModel(setupEngine, externalToolsOnly: true))
            {
                Owner = MainWindow
            };
            setupWindow.ShowDialog();
            _ = dashboardVm.RefreshAllAsync();
        };

        var prerequisiteInstaller = new PrerequisiteInstaller(voicemeeterManager);
        dashboardVm.RequestOpenPrerequisites += route =>
        {
            new PrerequisitesWindow(new PrerequisitesViewModel(prerequisiteInstaller, route, soundSyncIntegration))
            {
                Owner = MainWindow
            }.ShowDialog();
            _ = dashboardVm.RefreshAllAsync();
        };

        dashboardVm.RequestOpenDiagnostics += () =>
        {
            var diagVm = new DiagnosticsViewModel(diagnosticsService);
            var diagWin = new DiagnosticsWindow(diagVm)
            {
                Owner = MainWindow
            };
            diagWin.RequestNativeTests += () =>
            {
                var testWindow = new NativeTestWindow(
                    new NativeTestViewModel(audioService, soundSyncIntegration, controllerSession), showController: false)
                {
                    Owner = diagWin
                };
                testWindow.ShowDialog();
            };
            diagWin.ShowDialog();
        };

        // 3. Create & show MainWindow
        var mainWindow = new MainWindow(dashboardVm);
        mainWindow.Title = $"{AppBranding.Name} {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        bool stoppingBeforeClose = false;
        mainWindow.Closing += async (_, args) =>
        {
            if (!controllerSession.IsRunning) return;
            args.Cancel = true;
            if (stoppingBeforeClose) return;
            stoppingBeforeClose = true;
            await controllerSession.StopAsync();
            _ = mainWindow.Dispatcher.BeginInvoke(new Action(mainWindow.Close));
        };
        mainWindow.Closed += (_, _) => dashboardVm.StopMonitoring();
        Exit += (_, _) =>
        {
            stereoPlayback.Dispose();
            voicemeeterManager.Dispose();
        };
        mainWindow.Show();

        dashboardVm.StartMonitoring();
    }
}
