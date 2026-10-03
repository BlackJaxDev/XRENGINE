namespace XREngine.Input;

/// <summary>OpenVR texture API identifiers retained for compositor submission.</summary>
public enum RuntimeOpenVrTextureType
{
    Invalid = -1,
    DirectX = 0,
    OpenGL = 1,
    Vulkan = 2,
    IOSurface = 3,
    DirectX12 = 4,
    DXGISharedHandle = 5,
    Metal = 6,
}
