using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLStaticMeshSettings
{
    public int numVertices;
    public int numTriangles;
    public int numMaterials;
    public IntPtr vertices;
    public IntPtr triangles;
    public IntPtr materialIndices;
    public IntPtr materials;
}