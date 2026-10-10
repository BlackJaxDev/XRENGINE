using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>Replays explicitly CPU-owned work through its original collected command callbacks.</summary>
public interface IAuthoredIndexedCpuReplay
{
    void ReplayUnorderedCpuExempt(int renderPass, bool includeNonMesh);
    void ReplayOrderedMeshSource(BackendReadyFramePackage package, int renderPass, IRenderCommandMesh source);
}
