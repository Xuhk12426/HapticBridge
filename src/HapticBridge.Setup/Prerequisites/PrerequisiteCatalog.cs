using System.Diagnostics;
using HapticBridge.Audio.Voicemeeter;
using HapticBridge.Integrations.Apollo;
using HapticBridge.Integrations.DS4Windows;
using HapticBridge.Integrations.SoundSync;
using HapticBridge.Integrations.VIIPER;
using Microsoft.Win32;

namespace HapticBridge.Setup.Prerequisites;

public enum SetupRoute { DualSenseHaptics, ExternalTools }

public enum PrerequisiteState { Installed, Missing, NeedsAttention, RestartRequired }

internal enum PrerequisiteId { VisualCpp, Apollo, ViGEmBus, DS4Windows, VbCable, Voicemeeter, SoundSync }

internal sealed record PrerequisitePackage(
    PrerequisiteId Id, string Name, string Version, string Source, string Url, string Sha256,
    string Purpose, string? ArchiveInstaller = null, bool Portable = false, bool RestartAfterInstall = false);

public sealed record PrerequisiteItem(
    string Name, string Version, string Source, PrerequisiteState State, string Detail)
{
    public string Status => State switch
    {
        PrerequisiteState.Installed => "Installed",
        PrerequisiteState.Missing => "Available to install",
        PrerequisiteState.RestartRequired => "Restart required",
        _ => "Needs attention"
    };
}

internal static class PrerequisiteCatalog
{
    private static readonly PrerequisitePackage[] Shared =
    [
        new(PrerequisiteId.VisualCpp, "Microsoft Visual C++ x64", "14.50.35719.0",
            "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist/",
            "https://aka.ms/vs/18/release/14.50.35719/VC_redist.x64.exe",
            "8995548dfffcde7c49987029c764355612ba6850ee09a7b6f0fddc85bdc5c280",
            "Installs the Microsoft Visual C++ v14 runtime required by the controller tools."),
        new(PrerequisiteId.Apollo, "Apollo", "0.4.6",
            "https://github.com/ClassicOldSong/Apollo/releases/tag/v0.4.6",
            "https://github.com/ClassicOldSong/Apollo/releases/download/v0.4.6/Apollo-0.4.6.exe",
            "42b2aefaacb3474511517a56b96ee9f0517f30ac38b5dd2fda9fd5b478f5021a",
            "Streams games to Artemis and receives controller input."),
        new(PrerequisiteId.ViGEmBus, "ViGEmBus", "1.22.0",
            "https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0",
            "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe",
            "89220a7865076b342892f98865f3499fb7c4cfd673159e89d352c360fd014c6a",
            "Provides the virtual controller driver Apollo uses on Windows."),
        new(PrerequisiteId.DS4Windows, "DS4Windows with VIIPER", "5.0.12.0",
            "https://github.com/hbashton/DS4Windows/releases/tag/VIIPERRC4.6.6",
            "https://github.com/hbashton/DS4Windows/releases/download/VIIPERRC4.6.6/DS4Windows_5.0.12.0_Setup_x64.exe",
            "e4df0e8260b8850f19dc27a45033b24dbf033d436a5a872ace4542b2f317b436",
            $"Supplies VIIPER {ViiperControllerTest.TestedVersion} and its USB/IP driver. Windows may flag this unsigned release candidate.")
    ];

    private static readonly PrerequisitePackage[] External =
    [
        new(PrerequisiteId.VbCable, "VB-CABLE", "Pack 46",
            "https://vb-audio.com/Cable/",
            "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip",
            "bc44e8974191a5a2427d6c979e2dab08dbb09925f3f435dfa2f0ba3e45b6eb90",
            "Provides the virtual audio cable. VB-Audio requires a restart after installation.",
            ArchiveInstaller: "VBCABLE_Setup_x64.exe", RestartAfterInstall: true),
        new(PrerequisiteId.Voicemeeter, "Voicemeeter Banana", "2.1.3.0",
            "https://vb-audio.com/Voicemeeter/banana.htm",
            "https://download.vb-audio.com/Download_CABLE/VoicemeeterSetup_v2130.zip",
            "ee8b1f6cd4728233c8ae52897128c9af02367c091b88f55a1996d424e04d3965",
            "Routes game audio to your speakers and the haptics feed. VB-Audio requires a restart after installation.",
            ArchiveInstaller: "voicemeeterprosetup.exe", RestartAfterInstall: true),
        new(PrerequisiteId.SoundSync, "SoundSync", "2.0.0",
            "https://github.com/sugumar247/SoundSync/releases/tag/v2.0.0",
            "https://github.com/sugumar247/SoundSync/releases/download/v2.0.0/SoundSync.exe",
            "94005a1b3be2c044edb38953cbb2fdb4b7a2d21353f515bf2b288613fa8d5e2b",
            "Mirrors the audio feed to the controller. The portable app is placed in your Windows profile and stays closed.",
            Portable: true)
    ];

    public static IEnumerable<PrerequisitePackage> For(SetupRoute route) =>
        route == SetupRoute.ExternalTools ? Shared.Concat(External) : Shared;

    public static (PrerequisiteState, string?) Detect(PrerequisiteId id, VoicemeeterManager voicemeeter) => id switch
    {
        PrerequisiteId.VisualCpp => CheckVisualCpp(),
        PrerequisiteId.Apollo => CheckApollo(),
        PrerequisiteId.ViGEmBus => CheckService("ViGEmBus", "Nefarius Virtual Gamepad Emulation Bus", "ViGEm Bus Driver"),
        PrerequisiteId.DS4Windows => CheckDS4Windows(),
        PrerequisiteId.VbCable => CheckService("VBAudioVACMME", "VB-CABLE", "VB-Audio Virtual Cable"),
        PrerequisiteId.Voicemeeter => voicemeeter.CanLaunchBanana
            ? (PrerequisiteState.Installed, null) : CheckRegistration("Voicemeeter", "VB-Audio Voicemeeter"),
        PrerequisiteId.SoundSync => File.Exists(SoundSyncIntegration.LocateExecutablePath())
            ? (PrerequisiteState.Installed, null) : CheckRegistration("SoundSync"),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    private static (PrerequisiteState, string?) CheckVisualCpp()
    {
        using var runtime = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64")
            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
        if (runtime?.GetValue("Installed") is 1)
            return (PrerequisiteState.Installed, $"Windows has the x64 runtime {runtime.GetValue("Version")}.");

        return (PrerequisiteState.Missing, null);
    }

    private static (PrerequisiteState, string?) CheckApollo()
    {
        string path = Path.Combine(ApolloIntegration.LocateInstallDirectory(), "sunshine.exe");
        if (Path.IsPathFullyQualified(path) && File.Exists(path)) return (PrerequisiteState.Installed, null);
        return CheckRegistration("Apollo");
    }

    private static (PrerequisiteState, string?) CheckDS4Windows()
    {
        string directory = DS4WindowsIntegration.LocateInstallDirectory();
        if (string.IsNullOrEmpty(directory)) return CheckRegistration("DS4Windows");

        string executable = Path.Combine(directory, "VIIPER", "viiper.exe");
        if (!File.Exists(Path.Combine(directory, "DS4Windows.exe")) && !File.Exists(executable))
            return CheckRegistration("DS4Windows");

        if (!File.Exists(executable) ||
            FileVersionInfo.GetVersionInfo(executable).ProductVersion?.TrimStart('v') != ViiperControllerTest.TestedVersion)
            return (PrerequisiteState.NeedsAttention,
                $"An existing installation needs VIIPER {ViiperControllerTest.TestedVersion}. Open the vendor release to review it before changing versions.");

        if (CheckService("usbip2_ude").Item1 != PrerequisiteState.Installed ||
            CheckService("usbip2_filter").Item1 != PrerequisiteState.Installed)
            return (PrerequisiteState.NeedsAttention,
                "The matching VIIPER backend was found. Finish its USB/IP driver setup in DS4Windows or restart if prompted.");

        return (PrerequisiteState.Installed, null);
    }

    private static (PrerequisiteState, string?) CheckService(string name, params string[] registrationNames)
    {
        using var service = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        if (service is null) return registrationNames.Length == 0
            ? (PrerequisiteState.Missing, null) : CheckRegistration(registrationNames);
        return service.GetValue("Start") is 4
            ? (PrerequisiteState.NeedsAttention, "The driver is installed and disabled in Windows.")
            : (PrerequisiteState.Installed, null);
    }

    private static (PrerequisiteState, string?) CheckRegistration(params string[] names)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (string location in new[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using var uninstall = hive.OpenSubKey(location);
                if (uninstall is null) continue;
                foreach (string key in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(key);
                    if (entry?.GetValue("DisplayName") is string displayName &&
                        names.Any(name => displayName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                            displayName.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)))
                        return (PrerequisiteState.NeedsAttention,
                            "Windows lists an existing installation. Check its location or restart Windows if you just installed it.");
                }
            }
        }
        return (PrerequisiteState.Missing, null);
    }
}
