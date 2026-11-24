using System.Runtime.InteropServices;

namespace HapticBridge.Audio.Native;

[ComImport]
[Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
public class PolicyConfigClientComObject
{
}

[Guid("f8679f50-850a-4129-9ee4-e73c30e295f3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
    [PreserveSig] int GetDeviceFormat(string pszDeviceName, int bDefault, IntPtr ppFormat);
    [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
    [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod(string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
    [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
    [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
    [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr mode);
    [PreserveSig] int GetPropertyValue(string pszDeviceName, ref PROPERTYKEY pKey, out PROPVARIANT pv);
    [PreserveSig] int SetPropertyValue(string pszDeviceName, ref PROPERTYKEY pKey, ref PROPVARIANT pv);
    [PreserveSig] int SetDefaultEndpoint(string pszDeviceName, int role);
    [PreserveSig] int SetEndpointVisibility(string pszDeviceName, int bVisible);
}
