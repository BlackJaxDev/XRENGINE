namespace XREngine.Rendering.Commands;

public sealed partial class RenderCommandCollection : IAuthoredIndexedCpuReplay
{
    void IAuthoredIndexedCpuReplay.ReplayUnorderedCpuExempt(int renderPass, bool includeNonMesh)
        => RenderCPUExplicitlyExcludedCore(renderPass, includeNonMesh);

    void IAuthoredIndexedCpuReplay.ReplayOrderedMeshSource(BackendReadyFramePackage package, int renderPass,
        IRenderCommandMesh source)
    {
        using var renderingBufferScope = EnterRenderingBufferReadScope();
        if (!ReferenceEquals(package, _renderingBackendReadyPackage) || package.State != EBackendReadyFramePackageState.Published ||
            !package.TryGetPass(renderPass, out BackendReadyRenderPass pass))
            throw new NotSupportedException("RenderOrder.CpuReplayPackageChanged: direct replay requires the exact consumed source collection.");
        for (int index = 0; index < pass.CommandCount; index++)
        {
            RenderCommand command = package.GetPassCommand(in pass, index);
            if (!ReferenceEquals(command, source)) continue;
            if (!HasZeroAuthoredMeshInstances(command, source)) RenderWithGpuScope(command, renderPass);
            return;
        }
        throw new NotSupportedException("RenderOrder.CpuReplaySourceMissing: direct replay source is absent from the exact consumed pass.");
    }
}
