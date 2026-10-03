using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLInstancedMeshSettings
{
    public IPLScene subScene;
    public IPLMatrix4x4 transform;
}