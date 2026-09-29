using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost : IBrowserComputeSkinningCapability
{
    public void ConfigureComputeSkinning(BrowserResourceHandle mesh, BrowserSkinningData data)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(data);
        RequireSkinningMesh(mesh);
        WebGpuImports.ConfigureSkinning(_session, mesh.Packed, data.CopyPacket());
    }

    public void UpdateComputeSkinning(BrowserResourceHandle mesh, ReadOnlySpan<SkinPaletteMatrix> palette, ReadOnlySpan<Vector2> activeMorphs)
    {
        RequireReady();
        RequireSkinningMesh(mesh);
        // The import copies borrowed memory synchronously; it cannot retain these views.
        ReadOnlySpan<byte> paletteBytes = MemoryMarshal.AsBytes(palette);
        ReadOnlySpan<byte> morphBytes = MemoryMarshal.AsBytes(activeMorphs);
        WebGpuImports.UpdateSkinning(_session, mesh.Packed,
            MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(paletteBytes), paletteBytes.Length),
            MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(morphBytes), morphBytes.Length));
    }

    public void ReleaseComputeSkinning(BrowserResourceHandle mesh)
    {
        RequireReady();
        RequireSkinningMesh(mesh);
        WebGpuImports.ReleaseSkinning(_session, mesh.Packed);
    }

    private void RequireSkinningMesh(BrowserResourceHandle mesh)
    {
        if (!_resources.Contains(mesh.Packed))
            throw new InvalidOperationException("The deformation mesh must belong to this renderer.");
    }
}
