using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HapticBridge.Integrations.SoundSync;

public class SoundSyncDeviceItem
{
    public string DeviceId { get; set; } = string.Empty;
    public bool IsSelected { get; set; }
    public double Volume { get; set; } = 1.0;
    public double Delay { get; set; }
    public bool IsHidden { get; set; }
}

public class SoundSyncIntegration
{
    public static string ManagedExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "SoundSync", "SoundSync.exe");

    public static string LocateConfigDirectory()
    {
        string localApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundSync");
        if (Directory.Exists(localApp)) return localApp;

        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundSync");
        if (Directory.Exists(appData)) return appData;

        return localApp;
    }

    public static string LocateSettingsProfilePath()
    {
        return Path.Combine(LocateConfigDirectory(), "settings_profile.json");
    }

    public static string LocateExecutablePath()
    {
        // 1. Check running process
        var processes = Process.GetProcessesByName("SoundSync");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (File.Exists(path)) return path!;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        if (File.Exists(ManagedExecutablePath)) return ManagedExecutablePath;

        // 2. Check Downloads
        string userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SoundSync.exe");
        if (File.Exists(userDownloads)) return userDownloads;

        // 3. Check Program Files
        string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SoundSync", "SoundSync.exe");
        if (Path.IsPathFullyQualified(pf) && File.Exists(pf)) return pf;

        return string.Empty;
    }

    public virtual string ProfilePath => LocateSettingsProfilePath();

    public virtual bool IsInstalled => File.Exists(LocateExecutablePath()) || File.Exists(ProfilePath);

    public virtual bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName("SoundSync");
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        }
    }

    public string DetectedVersion
    {
        get
        {
            var exe = LocateExecutablePath();
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(exe);
                    if (!string.IsNullOrEmpty(vi.ProductVersion)) return vi.ProductVersion;
                    if (!string.IsNullOrEmpty(vi.FileVersion)) return vi.FileVersion;
                }
                catch { }
            }
            return string.Empty;
        }
    }

    public List<SoundSyncDeviceItem> ReadProfileDevices()
    {
        TryReadProfile(out var devices);
        return devices;
    }

    public virtual bool TryReadProfile(out List<SoundSyncDeviceItem> devices)
    {
        var list = new List<SoundSyncDeviceItem>();
        devices = [];

        try
        {
            if (JsonNode.Parse(File.ReadAllText(ProfilePath)) is not JsonArray entries) return false;
            foreach (var item in entries)
            {
                if (item is not JsonObject obj) return false;
                list.Add(new SoundSyncDeviceItem
                {
                    DeviceId = obj["DeviceId"]?.GetValue<string>() ?? string.Empty,
                    IsSelected = obj["IsSelected"]?.GetValue<bool>() ?? false,
                    Volume = obj["Volume"]?.GetValue<double>() ?? 1.0,
                    Delay = obj["Delay"]?.GetValue<double>() ?? 0,
                    IsHidden = obj["IsHidden"]?.GetValue<bool>() ?? false
                });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }

        devices = list;
        return true;
    }

    public bool IsCableOutputConfigured(string cableEndpointId) =>
        TryReadProfile(out var devices) && IsCableOutputConfigured(devices, cableEndpointId);

    public static bool IsCableOutputConfigured(IEnumerable<SoundSyncDeviceItem> devices, string cableEndpointId)
    {
        return !string.IsNullOrEmpty(cableEndpointId) && devices.Any(d =>
            MatchesEndpoint(d.DeviceId, cableEndpointId) &&
            d.IsSelected &&
            !d.IsHidden &&
            d.Volume >= 0.99);
    }

    public virtual bool ConfigureCableOutput(string cableEndpointId, double volume = 1.0)
    {
        if (string.IsNullOrWhiteSpace(cableEndpointId)) return false;
        var path = ProfilePath;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonArray arr) return false;

            bool found = false;
            foreach (var item in arr)
            {
                if (item is JsonObject obj)
                {
                    string id = obj["DeviceId"]?.GetValue<string>() ?? string.Empty;
                    if (MatchesEndpoint(id, cableEndpointId))
                    {
                        obj["IsSelected"] = true;
                        obj["Volume"] = volume;
                        obj["IsHidden"] = false;
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                // Preserve the identifier style already used by the saved profile.
                bool usesGuids = arr.OfType<JsonObject>()
                    .Any(item => Guid.TryParse(item["DeviceId"]?.GetValue<string>(), out _));
                var newObj = new JsonObject
                {
                    ["DeviceId"] = usesGuids && TryGetEndpointGuid(cableEndpointId, out var endpoint)
                        ? endpoint.ToString("B") : cableEndpointId,
                    ["IsSelected"] = true,
                    ["Volume"] = volume,
                    ["Delay"] = 0,
                    ["Bass"] = 0,
                    ["Mid"] = 0,
                    ["Treble"] = 0,
                    ["IsHidden"] = false,
                    ["AutoAlignDelay"] = false,
                    ["SyncVolumeWithDefault"] = false,
                    ["VolumeRatioToDefault"] = 1
                };
                arr.Add(newObj);
            }

            ConfigurationFile.Replace(path, file =>
                File.WriteAllText(file, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesEndpoint(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        TryGetEndpointGuid(left, out var leftGuid) && TryGetEndpointGuid(right, out var rightGuid) && leftGuid == rightGuid;

    private static bool TryGetEndpointGuid(string id, out Guid guid)
    {
        if (Guid.TryParse(id, out guid)) return true;
        const string renderPrefix = "{0.0.0.00000000}.";
        return id.StartsWith(renderPrefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(id[renderPrefix.Length..], out guid);
    }

    public void LaunchSoundSync()
    {
        if (IsRunning) return;
        var exe = LocateExecutablePath();
        if (File.Exists(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
