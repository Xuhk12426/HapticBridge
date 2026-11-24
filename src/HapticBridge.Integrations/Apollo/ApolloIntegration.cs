using System.Diagnostics;
using Microsoft.Win32;

namespace HapticBridge.Integrations.Apollo;

public class ApolloConfigModel
{
    public string Gamepad { get; set; } = string.Empty;
    public string VirtualSink { get; set; } = string.Empty;
    public bool IsReadable { get; set; }
    public Dictionary<string, string> RawSettings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled(string key) =>
        RawSettings.TryGetValue(key, out string? value) &&
        value.ToLowerInvariant() is "enabled" or "true" or "yes" or "on" or "1";

    public bool IsProperlyConfigured =>
        string.Equals(Gamepad, "ds4", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(VirtualSink) &&
        VirtualSink.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase);
}

public class ApolloIntegration
{
    public static string LocateInstallDirectory()
    {
        string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Apollo");
        if (Path.IsPathFullyQualified(defaultPath) && Directory.Exists(defaultPath)) return defaultPath;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Apollo")
                ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Apollo");

            if (key != null)
            {
                var uninst = key.GetValue("UninstallString")?.ToString();
                if (!string.IsNullOrEmpty(uninst))
                {
                    var dir = Path.GetDirectoryName(uninst.Trim('"'));
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
            }
        }
        catch { }

        return string.Empty;
    }

    public static string LocateConfigFilePath()
    {
        var installDir = LocateInstallDirectory();
        if (!string.IsNullOrEmpty(installDir))
        {
            var configPath = Path.Combine(installDir, "config", "sunshine.conf");
            if (File.Exists(configPath)) return configPath;
        }

        string programDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Apollo", "config", "sunshine.conf");
        if (File.Exists(programDataPath)) return programDataPath;

        return string.Empty;
    }

    public virtual bool IsInstalled => !string.IsNullOrEmpty(LocateInstallDirectory());
    public virtual string ConfigurationPath => LocateConfigFilePath();

    internal static string InstalledExecutable => Path.Combine(LocateInstallDirectory(), "sunshine.exe");

    internal static ProcessStartInfo CreateStartInfo() => new(Path.Combine(Environment.SystemDirectory, "sc.exe"), "start ApolloService")
    {
        UseShellExecute = true,
        Verb = "runas",
        WindowStyle = ProcessWindowStyle.Hidden,
        WorkingDirectory = Environment.SystemDirectory
    };

    internal static string? RunningHostExecutable(out bool running)
    {
        var processes = Process.GetProcessesByName("sunshine");
        running = processes.Length > 0;
        try
        {
            foreach (var process in processes)
            {
                if (ApolloLocalPeer.ReadImagePath(process.Id) is string path) return path;
            }
            return null;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public virtual bool IsRunning
    {
        get
        {
            var names = new[] { "sunshine", "sunshinesvc", "apollo" };
            return Process.GetProcesses().Any(p => names.Contains(p.ProcessName.ToLowerInvariant()));
        }
    }

    public string DetectedVersion
    {
        get
        {
            var installDir = LocateInstallDirectory();
            if (!string.IsNullOrEmpty(installDir))
            {
                var exe = Path.Combine(installDir, "sunshine.exe");
                if (File.Exists(exe))
                {
                    try
                    {
                        var vi = FileVersionInfo.GetVersionInfo(exe);
                        if (!string.IsNullOrEmpty(vi.ProductVersion)) return vi.ProductVersion;
                        if (!string.IsNullOrEmpty(vi.FileVersion)) return vi.FileVersion;
                    }
                    catch { }
                }
            }
            return string.Empty;
        }
    }

    public virtual ApolloConfigModel ReadConfiguration()
    {
        var model = new ApolloConfigModel();
        var path = ConfigurationPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return model;

        try
        {
            var lines = File.ReadAllLines(path);
            model.IsReadable = true;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';')) continue;

                int idx = trimmed.IndexOf('=');
                if (idx > 0)
                {
                    string key = trimmed[..idx].Trim();
                    string val = trimmed[(idx + 1)..].Split('#', 2)[0].Trim();
                    model.RawSettings[key] = val;

                    if (string.Equals(key, "gamepad", StringComparison.OrdinalIgnoreCase))
                    {
                        model.Gamepad = val;
                    }
                    else if (string.Equals(key, "virtual_sink", StringComparison.OrdinalIgnoreCase))
                    {
                        model.VirtualSink = val;
                    }
                }
            }
        }
        catch { }

        return model;
    }

    public virtual bool WriteConfiguration(string emulatedGamepad, string virtualSink)
    {
        var path = ConfigurationPath;
        if (string.IsNullOrEmpty(path)) return false;

        try
        {
            string text;
            System.Text.Encoding encoding;
            using (var reader = new StreamReader(path))
            {
                text = reader.ReadToEnd();
                encoding = reader.CurrentEncoding;
            }

            text = SetSetting(text, "gamepad", emulatedGamepad);
            text = SetSetting(text, "virtual_sink", virtualSink);
            ConfigurationFile.Replace(path, temporary => File.WriteAllText(temporary, text, encoding));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static ApolloConfigModel ReadHostConfiguration(string path) =>
        new LocalConfiguration(path).ReadConfiguration();

    public static void SaveControllerAudio(string path, bool enabled, bool separateGrips, Func<string, string> backUp)
        => UpdateHostConfiguration(path, backUp, original =>
        {
            string updated = SetSetting(original, "remote_input_audio", enabled ? "enabled" : "disabled");
            if (!enabled) return updated;
            updated = SetSetting(updated, "enable_input_only_mode", "enabled");
            updated = SetSetting(updated, "stream_audio", "enabled");
            return SetSetting(updated, "remote_input_haptics", separateGrips ? "enabled" : "disabled");
        });

    public static void SaveDs4(string path, Func<string, string> backUp) =>
        UpdateHostConfiguration(path, backUp, original => SetSetting(original, "gamepad", "ds4"));

    private static void UpdateHostConfiguration(string path, Func<string, string> backUp, Func<string, string> update)
    {
        string original;
        System.Text.Encoding encoding;
        using (var reader = new StreamReader(path))
        {
            original = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
        }

        string updated = update(original);
        if (original == updated) return;
        if (string.IsNullOrEmpty(backUp(path))) throw new IOException("Apollo’s settings could not be backed up.");
        ConfigurationFile.Replace(path, temporary =>
        {
            File.WriteAllText(temporary, updated, encoding);
            if (!string.Equals(File.ReadAllText(path), original, StringComparison.Ordinal))
                throw new IOException("Apollo’s settings changed elsewhere. Reopen this window and try again.");
        });
        if (!string.Equals(File.ReadAllText(path), updated, StringComparison.Ordinal))
            throw new IOException("Apollo’s saved settings could not be confirmed. Check the backup before retrying.");
    }

    private sealed class LocalConfiguration(string path) : ApolloIntegration
    {
        public override string ConfigurationPath => path;
    }

    private static string SetSetting(string text, string key, string value)
    {
        var setting = new System.Text.RegularExpressions.Regex(
            $@"^(?<prefix>[ \t]*{key}[ \t]*=[ \t]*)(?<value>[^#\r\n]*?)(?<suffix>[ \t]*(?:\#[^\r\n]*)?)(?=\r?$)",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var matches = setting.Matches(text);
        string line = $"{key} = {value}";
        if (matches.Count == 0)
        {
            string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            return text + (text.Length == 0 || text.EndsWith('\n') ? string.Empty : newline) + line + newline;
        }

        return setting.Replace(text, match =>
            match.Groups["prefix"].Value + value + match.Groups["suffix"].Value);
    }
}
