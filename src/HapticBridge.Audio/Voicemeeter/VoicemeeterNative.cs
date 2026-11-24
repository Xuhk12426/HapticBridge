using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace HapticBridge.Audio.Voicemeeter;

public class VoicemeeterNative : IDisposable
{
    private IntPtr _dllHandle = IntPtr.Zero;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int LoginDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int LogoutDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int IsParametersDirtyDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetVoicemeeterTypeDelegate(out int pType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetParameterFloatDelegate([MarshalAs(UnmanagedType.LPStr)] string szParamName, out float pValue);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int SetParameterFloatDelegate([MarshalAs(UnmanagedType.LPStr)] string szParamName, float Value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetParameterStringADelegate([MarshalAs(UnmanagedType.LPStr)] string szParamName, StringBuilder szString);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetParameterStringWDelegate([MarshalAs(UnmanagedType.LPStr)] string parameterName, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int SetParameterStringADelegate([MarshalAs(UnmanagedType.LPStr)] string szParamName, [MarshalAs(UnmanagedType.LPStr)] string szString);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int SetParameterStringWDelegate([MarshalAs(UnmanagedType.LPStr)] string parameterName, [MarshalAs(UnmanagedType.LPWStr)] string value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int SetParametersDelegate([MarshalAs(UnmanagedType.LPStr)] string szParamScript);

    public LoginDelegate? Login { get; private set; }
    public LogoutDelegate? Logout { get; private set; }
    public IsParametersDirtyDelegate? IsParametersDirty { get; private set; }
    public GetVoicemeeterTypeDelegate? GetVoicemeeterType { get; private set; }
    public GetParameterFloatDelegate? GetParameterFloat { get; private set; }
    public SetParameterFloatDelegate? SetParameterFloat { get; private set; }
    public GetParameterStringADelegate? GetParameterStringA { get; private set; }
    public GetParameterStringWDelegate? GetParameterStringW { get; private set; }
    public SetParameterStringADelegate? SetParameterStringA { get; private set; }
    public SetParameterStringWDelegate? SetParameterStringW { get; private set; }
    public SetParametersDelegate? SetParameters { get; private set; }

    public bool IsLoaded => _dllHandle != IntPtr.Zero;
    public string LoadedDllPath { get; private set; } = string.Empty;

    public static string LocateVoicemeeterRemoteDll()
    {
        // 1. Try known paths
        string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string native = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates = {
            Path.Combine(x86, "VB", "Voicemeeter", "VoicemeeterRemote64.dll"),
            Path.Combine(native, "VB", "Voicemeeter", "VoicemeeterRemote64.dll"),
            Path.Combine(x86, "VB", "Voicemeeter", "VoicemeeterRemote.dll")
        };

        foreach (var c in candidates)
        {
            if (Path.IsPathFullyQualified(c) && File.Exists(c)) return c;
        }

        // 2. Check Registry
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1428-0306}")
                ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1428-0306}");

            if (key != null)
            {
                var uninst = key.GetValue("UninstallString")?.ToString();
                if (!string.IsNullOrEmpty(uninst))
                {
                    var dir = Path.GetDirectoryName(uninst.Trim('"'));
                    if (!string.IsNullOrEmpty(dir))
                    {
                        var dll64 = Path.Combine(dir, "VoicemeeterRemote64.dll");
                        if (File.Exists(dll64)) return dll64;
                    }
                }
            }
        }
        catch
        {
            // Ignore registry read errors
        }

        return string.Empty;
    }

    public bool Load(string? dllPath = null)
    {
        if (IsLoaded) return true;

        dllPath ??= LocateVoicemeeterRemoteDll();
        if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath)) return false;

        try
        {
            _dllHandle = NativeLibrary.Load(dllPath);
            if (_dllHandle == IntPtr.Zero) return false;

            LoadedDllPath = dllPath;

            Login = Marshal.GetDelegateForFunctionPointer<LoginDelegate>(NativeLibrary.GetExport(_dllHandle, "VBVMR_Login"));
            Logout = Marshal.GetDelegateForFunctionPointer<LogoutDelegate>(NativeLibrary.GetExport(_dllHandle, "VBVMR_Logout"));
            IsParametersDirty = Marshal.GetDelegateForFunctionPointer<IsParametersDirtyDelegate>(NativeLibrary.GetExport(_dllHandle, "VBVMR_IsParametersDirty"));
            GetVoicemeeterType = Marshal.GetDelegateForFunctionPointer<GetVoicemeeterTypeDelegate>(NativeLibrary.GetExport(_dllHandle, "VBVMR_GetVoicemeeterType"));
            GetParameterFloat = Marshal.GetDelegateForFunctionPointer<GetParameterFloatDelegate>(NativeLibrary.GetExport(_dllHandle, "VBVMR_GetParameterFloat"));
            SetParameterFloat = Marshal.GetDelegateForFunctionPointer<SetParameterFloatDelegate>(NativeLibrary.GetExport(_dllHandle, "VBVMR_SetParameterFloat"));
            GetParameterStringA = Marshal.GetDelegateForFunctionPointer<GetParameterStringADelegate>(NativeLibrary.GetExport(_dllHandle, "VBVMR_GetParameterStringA"));
            GetParameterStringW = Marshal.GetDelegateForFunctionPointer<GetParameterStringWDelegate>(NativeLibrary.GetExport(_dllHandle, "VBVMR_GetParameterStringW"));
            SetParameterStringA = Marshal.GetDelegateForFunctionPointer<SetParameterStringADelegate>(NativeLibrary.GetExport(_dllHandle, "VBVMR_SetParameterStringA"));
            SetParameterStringW = Marshal.GetDelegateForFunctionPointer<SetParameterStringWDelegate>(NativeLibrary.GetExport(_dllHandle, "VBVMR_SetParameterStringW"));
            SetParameters = Marshal.GetDelegateForFunctionPointer<SetParametersDelegate>(NativeLibrary.GetExport(_dllHandle, "VBVMR_SetParameters"));

            return true;
        }
        catch
        {
            Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        if (_dllHandle != IntPtr.Zero)
        {
            try
            {
                NativeLibrary.Free(_dllHandle);
            }
            catch { }
            _dllHandle = IntPtr.Zero;
        }
    }
}
