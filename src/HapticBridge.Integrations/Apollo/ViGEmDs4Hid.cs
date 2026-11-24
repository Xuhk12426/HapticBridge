using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using HapticBridge.Integrations.Controllers;
using Microsoft.Win32.SafeHandles;

namespace HapticBridge.Integrations.Apollo;

internal sealed class ViGEmControllerUnavailableException(string message) : IOException(message);

internal static class ViGEmDs4Hid
{
    private const int ReportBytes = 64;
    private const int AccessDenied = 5;
    private const uint BufferSmall = 0x1A;
    private static readonly DeviceProperty InstanceId = new(new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), 256);

    public static FileStream OpenSingleGamepad()
    {
        HidD_GetHidGuid(out var hidClass);
        bool hidAccessDenied = false;
        var paths = ReadPresentPaths(hidClass)
            .Where(path => path.Contains("VID_054C&PID_05C4", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("VID_054C&PID_09CC", StringComparison.OrdinalIgnoreCase))
            .Where(path => ReadViGEmAncestry(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path =>
            {
                using var metadata = CreateFileW(path, 0, 3, 0, 3, 0, 0);
                if (!metadata.IsInvalid) return HasGamepadDescriptor(metadata);
                hidAccessDenied |= Marshal.GetLastPInvokeError() == AccessDenied;
                return false;
            })
            .Take(2).ToArray();

        if (paths.Length == 0 && hidAccessDenied) throw ControllerAccessDenied();
        if (paths.Length == 0) throw ControllerUnavailable();
        if (paths.Length > 1)
            throw new IOException("Several ViGEm DualShock 4 gamepads are present. Leave only the Apollo controller connected.");

        var handle = CreateFileW(paths[0], 0x80000000, 3, 0, 3, 0x40000000, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == AccessDenied) throw ControllerAccessDenied();
            if (error is 2 or 3 or 1167) throw ControllerUnavailable();
            throw new IOException($"Could not open the ViGEm DS4 controller (Windows error {error}).", new Win32Exception(error));
        }

        try
        {
            if (!ReadViGEmAncestry(paths[0]) || !HasGamepadDescriptor(handle))
                throw ControllerUnavailable();
            return new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static IOException ControllerAccessDenied() =>
        new("Windows denied access to the ViGEm DualShock 4. Check HidHide access in the DualSense Haptics tab.",
            new Win32Exception(AccessDenied));

    private static ViGEmControllerUnavailableException ControllerUnavailable() =>
        new("Waiting for Apollo's ViGEm DualShock 4. Press a controller button in Artemis and keep the stream open.");

    private static string[] ReadPresentPaths(Guid hidClass)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint result = CM_Get_Device_Interface_List_SizeW(out uint size, ref hidClass, 0, 0);
            if (result != 0 || size is < 1 or > 1048576)
                throw new IOException($"Could not size the Windows HID list (CONFIGRET 0x{result:X}).");
            var buffer = new ushort[size];
            result = CM_Get_Device_Interface_ListW(ref hidClass, 0, buffer, size, 0);
            if (result == BufferSmall) continue;
            if (result != 0) throw new IOException($"Could not enumerate Windows HID interfaces (CONFIGRET 0x{result:X}).");
            return new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan()))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        throw new IOException("Windows HID interfaces changed repeatedly during enumeration.");
    }

    private static bool ReadViGEmAncestry(string path)
    {
        var buffer = new byte[4096];
        uint size = (uint)buffer.Length;
        var key = InstanceId;
        if (CM_Get_Device_Interface_PropertyW(path, ref key, out uint type, buffer, ref size, 0) != 0 ||
            type != 0x12 || size < 2 || size > buffer.Length || size % 2 != 0)
            return false;
        string instanceId = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        return instanceId.Length > 0 && ControllerDetectionService.ReadUsbBus(instanceId) == ControllerDetectionService.UsbBus.ViGEm;
    }

    private static bool HasGamepadDescriptor(SafeFileHandle handle)
    {
        var attributes = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != 0x054C ||
            attributes.ProductId is not (0x05C4 or 0x09CC) ||
            !HidD_GetPreparsedData(handle, out nint data)) return false;
        try
        {
            return HidP_GetCaps(data, out var caps) == 0x00110000 &&
                caps is { UsagePage: 1, Usage: 5, InputReportByteLength: ReportBytes };
        }
        finally { HidD_FreePreparsedData(data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort Version;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HidCaps
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort UsagePage;
        [FieldOffset(4)] public ushort InputReportByteLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DeviceProperty(Guid Format, uint PropertyId);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid interfaceClass, nint deviceId, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid interfaceClass, nint deviceId,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] ushort[] buffer, uint size, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_PropertyW(string path, ref DeviceProperty property,
        out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    [DllImport("Kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("hid.dll", ExactSpelling = true)]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint data);

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(nint data);

    [DllImport("hid.dll", ExactSpelling = true)]
    private static extern int HidP_GetCaps(nint data, out HidCaps caps);
}
