using HapticBridge.Integrations.Apollo;
using HapticBridge.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HapticBridge.Tests;

[TestClass]
public sealed class ApolloSettingsTests
{
    [TestMethod]
    public void UsesConfiguredBasePortForLoopbackStatusAndBrowser()
    {
        var (serverInfo, webUi) = ApolloHostClient.GetLocalEndpoints("48089");
        Assert.AreEqual("http://127.0.0.1:48089/serverinfo", serverInfo.AbsoluteUri);
        Assert.AreEqual("https://localhost:48090/", webUi.AbsoluteUri);
        Assert.AreEqual(47990, ApolloHostClient.GetLocalEndpoints("65535").WebUi.Port);
        Assert.AreEqual(47990, ApolloHostClient.GetLocalEndpoints("http://other-host").WebUi.Port);
    }

    [TestMethod]
    [DataRow("", 1)]
    [DataRow("<HapticBridgeRemoteInputAudio>disabled</HapticBridgeRemoteInputAudio>", 2)]
    [DataRow("<HapticBridgeRemoteInputAudio>enabled</HapticBridgeRemoteInputAudio>", 3)]
    public void RecognizesTheRunningApolloCapability(string marker, int mode)
    {
        string response = $"""<root status_code="200"><PairStatus>0</PairStatus>{marker}</root>""";
        Assert.AreEqual((ApolloAudioMode)mode, ApolloHostClient.ReadAudioMode(response));
        Assert.AreEqual(ApolloAudioMode.Unreachable, ApolloHostClient.ReadAudioMode("<html />"));
    }

    [TestMethod]
    public void PhoneAddressUsesTheHostPortAndInstalledLaunchUsesWindowsServiceControl()
    {
        var ip = System.Net.IPAddress.Parse("192.168.50.2");
        Assert.AreEqual("192.168.50.2", ApolloHostClient.FormatLanAddress(ip, 47989));
        Assert.AreEqual("192.168.50.2:48189", ApolloHostClient.FormatLanAddress(ip, 48189));
        var launch = ApolloIntegration.CreateStartInfo();
        Assert.AreEqual("start ApolloService", launch.Arguments);
        Assert.AreEqual("runas", launch.Verb);
        Assert.AreEqual(Environment.SystemDirectory, launch.WorkingDirectory);
        Assert.IsTrue(launch.UseShellExecute);
    }

    [TestMethod]
    public async Task CanLaunchStoppedHostAndOpenPairingAfterItResponds()
    {
        var origin = new Uri("https://localhost:48190/");
        var host = new ApolloHostStatus(origin, ApolloAudioMode.Unreachable, true)
        {
            CanStart = true,
            Addresses = ["192.168.50.2:48189"]
        };
        Uri? opened = null;
        ApolloManagementPage? requested = null;
        string? copied = null;
        int starts = 0;
        var model = new ApolloSettingsViewModel(() => Task.FromResult(host), uri => opened = uri, () =>
        {
            starts++;
            host = host with { AudioMode = ApolloAudioMode.Enabled, IsRunning = true, HostName = "Test PC" };
        }, address => copied = address);
        model.ManagementRequested += (_, page) => requested = page;

        await model.RefreshAsync();
        Assert.IsTrue(model.StartHostCommand.CanExecute(null));
        Assert.IsFalse(model.OpenPairingCommand.CanExecute(null));
        model.CopyAddressCommand.Execute(null);
        Assert.AreEqual("192.168.50.2:48189", copied);
        await model.StartHostAsync();
        await model.StartHostAsync();
        Assert.AreEqual(1, starts);
        StringAssert.Contains(model.HostStatus, "Test PC");
        Assert.IsFalse(model.StartHostCommand.CanExecute(null));
        Assert.IsTrue(model.OpenPairingCommand.CanExecute(null));
        model.OpenPairingCommand.Execute(null);
        Assert.AreEqual(ApolloManagementPage.Devices, requested);
        model.StopHostCommand.Execute(null);
        Assert.AreEqual(ApolloManagementPage.Stop, requested);
        Assert.IsNull(opened);
    }

    [TestMethod]
    public async Task OpensTheFocusedSettingOnlyOnTheCustomHost()
    {
        var webUi = new Uri("https://localhost:47990/");
        Uri? opened = null;
        ApolloManagementPage? requested = null;
        var current = new ApolloHostStatus(webUi, ApolloAudioMode.Unsupported, IsInstalled: true);
        var model = new ApolloSettingsViewModel(() => Task.FromResult(current), uri => opened = uri);
        model.ManagementRequested += (_, page) => requested = page;
        await model.RefreshAsync();

        Assert.IsFalse(model.OpenModeSettingsCommand.CanExecute(null));
        Assert.IsTrue(model.OpenWebUiCommand.CanExecute(null));
        model.OpenWebUiCommand.Execute(null);
        Assert.AreEqual(webUi, opened);

        current = current with { AudioMode = ApolloAudioMode.Available };
        await model.RefreshAsync();
        Assert.IsTrue(model.OpenModeSettingsCommand.CanExecute(null));
        model.OpenModeSettingsCommand.Execute(null);
        Assert.AreEqual(ApolloManagementPage.Audio, requested);
        Assert.AreEqual(webUi, opened);
    }
}
