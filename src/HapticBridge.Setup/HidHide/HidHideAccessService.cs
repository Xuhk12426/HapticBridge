using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HapticBridge.Core;
using HapticBridge.Setup.Backups;
using Microsoft.Win32.SafeHandles;

namespace HapticBridge.Setup.HidHide;

internal sealed record HidHideAccess(bool HidingEnabled, bool Inverse, bool Listed)
{
    public bool CanReadHiddenDevices => !HidingEnabled || Listed != Inverse;
}

internal interface IHidHideSession : IDisposable
{
    bool IsHidingEnabled { get; }
    bool IsInverse { get; }
    string[] ReadApplications();
    void WriteApplications(string[] applications);
}

public sealed class HidHideAccessService
{
    private readonly Func<IHidHideSession?> _open;
    private readonly Func<string> _getDeviceImagePath;
    private readonly ConfigurationBackupService _backups;

    public HidHideAccessService(ConfigurationBackupService backups)
        : this(backups, Environment.ProcessPath ?? string.Empty, HidHideSession.Open, HidHideSession.GetCurrentImagePath) { }

    internal HidHideAccessService(ConfigurationBackupService backups, string applicationPath,
        Func<IHidHideSession?> open, Func<string> getDeviceImagePath)
    {
        _backups = backups;
        ApplicationPath = applicationPath;
        _open = open;
        _getDeviceImagePath = getDeviceImagePath;
    }

    internal string ApplicationPath { get; }

    internal HidHideAccess? Inspect()
    {
        using var session = _open();
        if (session is null) return null;
        string image = GetApplicationImage();
        return ReadAccess(session, session.ReadApplications(), image);
    }

    internal (bool Verified, string BackupPath) AllowAccess(bool approvedInverse)
    {
        using var session = _open() ?? throw new IOException("The HidHide driver is no longer available.");
        string image = GetApplicationImage();
        var applications = session.ReadApplications();
        var current = ReadAccess(session, applications, image);
        if (current.CanReadHiddenDevices) return (true, string.Empty);
        if (current.Inverse != approvedInverse)
            throw new IOException("HidHide mode changed. Check again before applying this repair.");

        string[] updated = current.Inverse
            ? applications.Where(path => !SamePath(path, image)).ToArray()
            : [.. applications, image];
        string backup = _backups.SaveSnapshot("HidHide", "Applications.json", JsonSerializer.Serialize(new
        {
            current.HidingEnabled,
            current.Inverse,
            Applications = applications
        }));

        try
        {
            session.WriteApplications(updated);
            var readback = session.ReadApplications();
            var access = ReadAccess(session, readback, image);
            return (access == (current with { Listed = !current.Listed }) &&
                updated.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(readback), backup);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            return (false, backup);
        }
    }

    private string GetApplicationImage()
    {
        const string fileName = AppBranding.ExecutableName;
        if (!Path.IsPathFullyQualified(ApplicationPath) || !SamePath(Path.GetFileName(ApplicationPath), fileName))
            throw new IOException($"Launch {fileName} to configure its HidHide access.");

        string image = _getDeviceImagePath();
        if (!image.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase) || !SamePath(Path.GetFileName(image), fileName))
            throw new IOException("Windows could not identify the running installer executable for HidHide.");

        return image;
    }

    private static HidHideAccess ReadAccess(IHidHideSession session, string[] applications, string image) =>
        new(session.IsHidingEnabled, session.IsInverse, applications.Contains(image, StringComparer.OrdinalIgnoreCase));

    private static bool SamePath(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}

internal sealed class HidHideSession(SafeFileHandle handle) : IHidHideSession
{
    // HidHideIoctlContract.h uses METHOD_BUFFERED and FILE_READ_DATA for these operations.
    private const uint GetApplications = 2048;
    private const uint SetApplications = 2049;
    private const uint GetActive = 2052;
    private const uint GetInverse = 2054;
    private const int MaximumListBytes = short.MaxValue;

    public static IHidHideSession? Open()
    {
        var device = CreateFileW(@"\\.\HidHide", 0x80000000, 7, 0, 3, 0x80, 0);
        if (!device.IsInvalid) return new HidHideSession(device);
        int error = Marshal.GetLastPInvokeError();
        device.Dispose();
        return error is 2 or 3 ? null : throw new Win32Exception(error);
    }

    public static string GetCurrentImagePath()
    {
        var result = new StringBuilder(32768);
        int size = result.Capacity;
        if (!QueryFullProcessImageNameW(GetCurrentProcess(), 1, result, ref size))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return result.ToString();
    }

    public bool IsHidingEnabled => ReadFlag(GetActive);
    public bool IsInverse => ReadFlag(GetInverse);

    public string[] ReadApplications()
    {
        int size = Control(GetApplications);
        if (size is < 2 or >= MaximumListBytes || size % 2 != 0)
            throw new IOException("HidHide returned an invalid application list size.");
        var buffer = new byte[size];
        Control(GetApplications, output: buffer);
        string list = Encoding.Unicode.GetString(buffer);
        if (list[^1] != '\0') throw new IOException("HidHide returned an incomplete application list.");
        return list.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public void WriteApplications(string[] applications)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(string.Join('\0', applications) + "\0\0");
        if (bytes.Length >= MaximumListBytes) throw new IOException("HidHide's application list is full.");
        Control(SetApplications, input: bytes);
    }

    private bool ReadFlag(uint function)
    {
        var value = new byte[1];
        if (Control(function, output: value) != 1) throw new IOException("HidHide returned an invalid setting.");
        return value[0] != 0;
    }

    private int Control(uint function, byte[]? input = null, byte[]? output = null)
    {
        uint code = (32769u << 16) | (1u << 14) | (function << 2);
        if (!DeviceIoControl(handle, code, input, input?.Length ?? 0, output, output?.Length ?? 0, out int count, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return count;
    }

    public void Dispose() => handle.Dispose();

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputLength,
        [Out] byte[]? output, int outputLength, out int count, nint overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder path, ref int size);
}
