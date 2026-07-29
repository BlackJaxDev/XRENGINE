using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Backend-neutral render-state classification referenced by canonical draws.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedRenderStateRecord
{
    public uint StateClass;
    public uint PrimitiveTopology;
    public uint CoverageMode;
    public uint CullMode;
    public uint DepthMode;
    public uint BlendMode;
    public uint ColorWriteMask;
    public uint Flags;
}
