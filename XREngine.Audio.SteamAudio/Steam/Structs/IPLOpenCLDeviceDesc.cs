using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLOpenCLDeviceDesc
{
    public IntPtr platform;
    public IntPtr platformName;
    public IntPtr platformVendor;
    public IntPtr platformVersion;
    public IntPtr device;
    public IntPtr deviceName;
    public IntPtr deviceVendor;
    public IntPtr deviceVersion;
    public IPLOpenCLDeviceType type;
    public int numConvolutionCUs;
    public int numIRUpdateCUs;
    public int granularity;
    public float perfScore;
}