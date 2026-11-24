using System.Diagnostics;
using System.Text;
using System.Xml;
using HapticBridge.Core.Models;
using HapticBridge.Core.Services;

namespace HapticBridge.Audio.Voicemeeter;

public class VoicemeeterManager : IVoicemeeterManager, IDisposable
{
    private const string ListeningDevice = "Bus[0].device.name";
    private const string ListeningGain = "Bus[0].Gain";
    private const string ListeningMute = "Bus[0].Mute";
    private readonly VoicemeeterNative _native = new();
    private readonly object _remoteLock = new();
    private bool _loggedIn;
    private bool _isDisposed;

    public bool IsInstalled => File.Exists(DllPath);

    public bool IsRunning
    {
        get
        {
            foreach (string name in new[] { "voicemeeterpro", "voicemeeter8", "voicemeeter" })
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var process in processes) process.Dispose();
                if (processes.Length > 0) return true;
            }

            return false;
        }
    }

    public string DllPath => VoicemeeterNative.LocateVoicemeeterRemoteDll();
    private string BananaExecutable => Path.Combine(Path.GetDirectoryName(DllPath) ?? string.Empty, "voicemeeterpro.exe");
    public bool CanLaunchBanana => IsInstalled && Path.IsPathFullyQualified(BananaExecutable) && File.Exists(BananaExecutable);

    public VoicemeeterRoutingState GetLiveRoutingState()
    {
        lock (_remoteLock)
        {
            var state = new VoicemeeterRoutingState();
            if (!RefreshRemoteParameters()) return state;

            state.IsConnected = true;
            if (_native.GetVoicemeeterType!(out int type) == 0)
                state.VoicemeeterType = type;

            var read = _native.GetParameterFloat!;
            if (read("Strip[3].A1", out float a1) == 0 &&
                read("Strip[3].A2", out float a2) == 0 &&
                read("Strip[3].B1", out float b1) == 0 &&
                read("Strip[3].B2", out float b2) == 0 &&
                read("Bus[1].Mode.Composite", out float composite) == 0 &&
                read("Patch.Composite[0]", out float channel1) == 0 &&
                read("Patch.Composite[1]", out float channel2) == 0)
            {
                state.HasReferenceParameters = true;
                state.StripVaioA1 = a1 > 0.5f;
                state.StripVaioA2 = a2 > 0.5f;
                state.StripVaioB1 = b1 > 0.5f;
                state.StripVaioB2 = b2 > 0.5f;
                state.BusA2Composite = composite > 0.5f;
                state.PatchCompositeCh1 = (int)channel1;
                state.PatchCompositeCh2 = (int)channel2;
            }

            bool readA1 = TryReadDeviceName(ListeningDevice, out string a1Name);
            bool readA2 = TryReadDeviceName("Bus[1].device.name", out string a2Name);
            state.HasOutputDeviceNames = readA1 && readA2;
            state.PhysicalOutA1Device = a1Name;
            state.PhysicalOutA2Device = a2Name;
            return state;
        }
    }

    public (string DeviceName, int? VolumePercent)? GetListeningOutput()
    {
        lock (_remoteLock)
        {
            if (!IsRunning || !RefreshRemoteParameters() || !TryReadDeviceName(ListeningDevice, out string name))
                return null;

            var read = _native.GetParameterFloat!;
            if (read(ListeningGain, out float gain) != 0 || !float.IsFinite(gain) ||
                read(ListeningMute, out float mute) != 0 || !float.IsFinite(mute))
                return (name, null);

            // 100% is unity gain. Report a muted bus as 0% and preserve boosts set in the mixer.
            int percent = mute > 0.5f ? 0 : (int)Math.Clamp(Math.Round(100 * Math.Pow(10, gain / 20.0)), 1, 100);
            return (name, percent);
        }
    }

    public bool TrySetPhysicalOutput(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return false;

        lock (_remoteLock)
        {
            return IsRunning && RefreshRemoteParameters() &&
                _native.SetParameterStringW!("Bus[0].device.wdm", deviceName) == 0;
        }
    }

    public bool TrySetListeningVolume(string deviceName, int volumePercent)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || volumePercent is < 0 or > 100) return false;

        lock (_remoteLock)
        {
            if (!RefreshRemoteParameters() || !TryReadDeviceName(ListeningDevice, out string currentDevice) ||
                !string.Equals(deviceName, currentDevice, StringComparison.OrdinalIgnoreCase))
                return false;

            if (volumePercent == 0)
                return _native.SetParameterFloat!(ListeningMute, 1) == 0;

            double gain = 20 * Math.Log10(volumePercent / 100.0);
            string command = FormattableString.Invariant($"{ListeningGain} = {gain:F3}; {ListeningMute} = 0;");
            return _native.SetParameters!(command) == 0;
        }
    }

    public bool ConfigureReferenceRouting(string? physicalOutputA1Name, string cableOutputA2Name)
    {
        if (string.IsNullOrWhiteSpace(cableOutputA2Name)) return false;

        lock (_remoteLock)
        {
            return RefreshRemoteParameters() &&
                _native.GetVoicemeeterType!(out int type) == 0 && type == 2 &&
                _native.SetParameters!("""
                    Strip[3].A1 = 1;
                    Strip[3].A2 = 1;
                    Strip[3].B1 = 0;
                    Strip[3].B2 = 0;
                    Bus[1].Mode.Composite = 1;
                    Patch.Composite[0] = 11;
                    Patch.Composite[1] = 12;
                    """) == 0 &&
                (physicalOutputA1Name is null || _native.SetParameterStringW!("Bus[0].device.wdm", physicalOutputA1Name) == 0) &&
                _native.SetParameterStringW!("Bus[1].device.wdm", cableOutputA2Name) == 0;
        }
    }

    public bool CreateConfigurationBackup(string backupDestinationPath)
    {
        string path = Path.GetFullPath(backupDestinationPath);
        try
        {
            if (!Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase) || File.Exists(path))
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            lock (_remoteLock)
            {
                if (!RefreshRemoteParameters() || _native.SetParameterStringW!("Command.Save", path) != 0)
                    return false;

                // A successful Remote API return only means the save was queued.
                var timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(2))
                {
                    if (IsVoicemeeterSettingsFile(path)) return true;
                    Thread.Sleep(50);
                }

                return false;
            }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public bool RestoreConfiguration(string backupSourcePath)
    {
        string path = Path.GetFullPath(backupSourcePath);
        lock (_remoteLock)
        {
            return IsVoicemeeterSettingsFile(path) && RefreshRemoteParameters() &&
                _native.SetParameterStringW!("Command.Load", path) == 0;
        }
    }

    public void LaunchVoicemeeter()
    {
        if (IsRunning || !CanLaunchBanana) return;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(BananaExecutable) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        lock (_remoteLock)
        {
            if (_isDisposed) return;

            _isDisposed = true;
            if (_loggedIn) _native.Logout!();
            _native.Dispose();
        }
    }

    private bool RefreshRemoteParameters()
    {
        if (_isDisposed) return false;
        if (!_loggedIn)
        {
            if (!_native.Load(DllPath) || _native.Login!() < 0) return false;
            _loggedIn = true;
        }

        return _native.IsParametersDirty!() >= 0;
    }

    private bool TryReadDeviceName(string parameter, out string name)
    {
        var value = new StringBuilder(512);
        bool success = _native.GetParameterStringW!(parameter, value) == 0;
        name = success ? value.ToString() : string.Empty;
        return success;
    }

    private static bool IsVoicemeeterSettingsFile(string path)
    {
        try
        {
            using var reader = XmlReader.Create(path);
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "VBAudioVoicemeeterSettings")
                return false;

            while (reader.Read()) { }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (XmlException) { return false; }
    }
}
