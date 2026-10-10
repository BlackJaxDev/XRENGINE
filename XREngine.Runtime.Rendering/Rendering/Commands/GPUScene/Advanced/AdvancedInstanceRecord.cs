using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Frame-slot instance state shared by desktop and XR render paths.
/// </summary>
[XREngine.Rendering.Shaders.GpuRecord("XRAdvancedInstanceRecord")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedInstanceRecord
{
    public Matrix4x4 CurrentWorld;
    public Matrix4x4 PreviousWorld;
    public Vector4 BoundsSphere;
    public Vector4 BoundsMin;
    public Vector4 BoundsMax;
    public AdvancedGpuHandle Animation;
    public AdvancedGpuHandle Deformation;
    public EAdvancedInstanceVisibilityFlags VisibilityFlags;
    public uint LodLevel;
    public uint LayerMask;
    public uint RenderPassMask;
    public uint CurrentFrameSlot;
    public uint PreviousFrameSlot;
    /// <summary>Browser publications retain the authored source pass for exact deferred decal receiver eligibility.</summary>
    public uint Reserved0;
    public uint Reserved1;
}
