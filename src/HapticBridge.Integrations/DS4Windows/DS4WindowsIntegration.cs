using System.Diagnostics;
using System.Xml.Linq;

namespace HapticBridge.Integrations.DS4Windows;

public class DS4WindowsConfigModel
{
    public bool UseMoonlight { get; set; }
    public string OutputContDevice { get; set; } = string.Empty;
    public bool DualSenseAudioHapticsEnabled { get; set; }
    public bool DualSenseAdaptiveTriggersEnabled { get; set; }
    public bool HasGlobalConfig { get; set; }
    public bool HasDefaultProfile { get; set; }

    public bool IsProperlyConfigured =>
        UseMoonlight &&
        string.Equals(OutputContDevice, "ViiperDualSense", StringComparison.OrdinalIgnoreCase) &&
        DualSenseAudioHapticsEnabled;
}

public class DS4WindowsIntegration
{
    public static string LocateInstallDirectory()
    {
        string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DS4Windows");
        if (Path.IsPathFullyQualified(defaultPath) && Directory.Exists(defaultPath)) return defaultPath;

        string localApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DS4Windows");
        if (Directory.Exists(localApp) && File.Exists(Path.Combine(localApp, "DS4Windows.exe"))) return localApp;

        return string.Empty;
    }

    public static string LocateAppDataDirectory()
    {
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DS4Windows");
        if (Directory.Exists(appData)) return appData;

        string localApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DS4Windows");
        if (Directory.Exists(localApp)) return localApp;

        return appData;
    }

    public virtual bool IsInstalled => !string.IsNullOrEmpty(LocateInstallDirectory()) || Directory.Exists(LocateAppDataDirectory());
    public virtual string ConfigurationDirectory => LocateAppDataDirectory();

    public virtual bool IsRunning
    {
        get
        {
            var names = new[] { "ds4windows" };
            return Process.GetProcesses().Any(p => names.Contains(p.ProcessName.ToLowerInvariant()));
        }
    }

    public virtual bool IsViiperRunning
    {
        get
        {
            var names = new[] { "viiper" };
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
                var exe = Path.Combine(installDir, "DS4Windows.exe");
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

    public virtual DS4WindowsConfigModel ReadConfiguration()
    {
        var model = new DS4WindowsConfigModel();
        string appDataDir = ConfigurationDirectory;

        // 1. Check Profiles.xml for UseMoonlight
        string profilesXmlPath = Path.Combine(appDataDir, "Profiles.xml");
        if (File.Exists(profilesXmlPath))
        {
            try
            {
                var doc = XDocument.Load(profilesXmlPath);
                model.HasGlobalConfig = doc.Root is not null;
                var elem = doc.Root?.Element("UseMoonlight");
                if (elem != null && bool.TryParse(elem.Value, out bool useM))
                {
                    model.UseMoonlight = useM;
                }
            }
            catch { }
        }

        // 2. Check active profile Default.xml
        string defaultProfilePath = Path.Combine(appDataDir, "Profiles", "Default.xml");
        if (File.Exists(defaultProfilePath))
        {
            try
            {
                var doc = XDocument.Load(defaultProfilePath);
                var root = doc.Root;
                if (root != null)
                {
                    model.HasDefaultProfile = true;
                    var devElem = root.Element("OutputContDevice");
                    if (devElem != null)
                    {
                        model.OutputContDevice = devElem.Value.Trim();
                    }

                    var hapticElem = root.Element("Switch2DualSenseAudioHapticsEnabled");
                    if (hapticElem != null && bool.TryParse(hapticElem.Value, out bool haptics))
                    {
                        model.DualSenseAudioHapticsEnabled = haptics;
                    }

                    var triggerElem = root.Element("Switch2DualSenseAdaptiveTriggersEnabled");
                    if (triggerElem != null && bool.TryParse(triggerElem.Value, out bool triggers))
                    {
                        model.DualSenseAdaptiveTriggersEnabled = triggers;
                    }
                }
            }
            catch { }
        }

        return model;
    }

    public virtual bool ConfigureDualSenseAndMoonlight(bool enableMoonlight = true, string outputController = "ViiperDualSense")
    {
        string appDataDir = ConfigurationDirectory;

        try
        {
            // 1. Update Profiles.xml
            string profilesXmlPath = Path.Combine(appDataDir, "Profiles.xml");
            if (File.Exists(profilesXmlPath))
            {
                var doc = XDocument.Load(profilesXmlPath);
                var root = doc.Root;
                if (root != null)
                {
                    var elem = root.Element("UseMoonlight");
                    if (elem == null)
                    {
                        root.Add(new XElement("UseMoonlight", enableMoonlight.ToString().ToLowerInvariant()));
                    }
                    else
                    {
                        elem.Value = enableMoonlight.ToString().ToLowerInvariant();
                    }
                    ConfigurationFile.Replace(profilesXmlPath, file => doc.Save(file));
                }
            }

            // 2. Update Default.xml
            string profilesDir = Path.Combine(appDataDir, "Profiles");
            if (!Directory.Exists(profilesDir)) Directory.CreateDirectory(profilesDir);

            string defaultProfilePath = Path.Combine(profilesDir, "Default.xml");
            if (File.Exists(defaultProfilePath))
            {
                var doc = XDocument.Load(defaultProfilePath);
                var root = doc.Root;
                if (root != null)
                {
                    SetOrAddElement(root, "OutputContDevice", outputController);
                    SetOrAddElement(root, "Switch2DualSenseAudioHapticsEnabled", "true");
                    SetOrAddElement(root, "Switch2DualSenseAdaptiveTriggersEnabled", "true");
                    ConfigurationFile.Replace(defaultProfilePath, file => doc.Save(file));
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SetOrAddElement(XElement parent, string name, string value)
    {
        var elem = parent.Element(name);
        if (elem == null)
        {
            parent.Add(new XElement(name, value));
        }
        else
        {
            elem.Value = value;
        }
    }
}
