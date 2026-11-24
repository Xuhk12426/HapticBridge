using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using HapticBridge.Integrations.Apollo;
using HapticBridge.Setup.Backups;

namespace HapticBridge.UI;

internal static class ApolloDs4Setup
{
    internal const string Command = "--configure-installed-apollo-ds4";

    private static string StandardConfig => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Apollo", "config", "sunshine.conf");

    internal static void Configure(ApolloHostStatus host)
    {
        string path = host.ConfigurationPath ?? throw new IOException("Apollo’s configuration file was not found.");
        if (string.Equals(ApolloIntegration.ReadHostConfiguration(path).Gamepad, "ds4", StringComparison.OrdinalIgnoreCase))
            return;

        try { Save(path); }
        catch (UnauthorizedAccessException) when (
            string.Equals(path, StandardConfig, StringComparison.OrdinalIgnoreCase) &&
            host.TrustedInstalledExecutable is not null)
        {
            using var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Command)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.SystemDirectory
            }) ?? throw new IOException("Windows did not start the Apollo configuration helper.");
            helper.WaitForExit();
            if (helper.ExitCode != 0)
                throw new IOException("Could not save Apollo’s controller setting. Check the configuration file and its permissions.");
        }

        if (!string.Equals(ApolloIntegration.ReadHostConfiguration(path).Gamepad, "ds4", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Apollo’s DualShock 4 setting could not be verified.");
    }

    internal static int RunHelper()
    {
        try
        {
            string path = StandardConfig;
            if (!Path.IsPathFullyQualified(path)) return 1;
            for (string? entry = path; entry is not null; entry = Path.GetDirectoryName(entry))
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) return 1;
            Save(path);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return 1;
        }
    }

    private static void Save(string path) => ApolloIntegration.SaveDs4(path,
        source => new ConfigurationBackupService().CreateBackup("Apollo", source));
}
