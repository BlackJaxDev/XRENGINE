using XREngine.Input.Devices;
using XREngine.Input.Devices.DirectX;

namespace XREngine.Input;

/// <summary>Installs the Windows XInput gamepad factory in a desktop application.</summary>
public static class XInputBackend
{
    public static void Register()
        => InputBackendRegistry.RegisterGamepad(EInputType.XInput, static index => new DXGamepad(index));
}
