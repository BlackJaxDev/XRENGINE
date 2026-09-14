using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Components.Lights;

/// <summary>
/// A legacy display command reserves its exact mirror source at collection time. The renderer
/// owns the later GPU fence retain; this reservation prevents the capture owner from rewriting it
/// before the collected command either executes or is rejected.
/// </summary>
internal sealed class MirrorDisplayCommand : RenderCommandMesh3D, IRenderCommandCollectedResource
{
    private readonly MirrorCaptureSlot _slot;

    internal MirrorDisplayCommand(int renderPass, XRMeshRenderer renderer, Matrix4x4 worldMatrix, MirrorCaptureSlot slot)
        : base(renderPass, renderer, worldMatrix)
        => _slot = slot;

    public bool TryRetainCollectedResource()
        => _slot.TryRetainDisplayResource();

    public void ReleaseCollectedResource()
        => _slot.ReleaseDisplayResource();

    public override void Render()
    {
        // Collection retained the exact generation before command execution. Transfer one
        // additional reserved retain to the GPU reader before issuing any draw commands.
        if (!_slot.TryRetainGpuReader())
            return;
        try
        {
            base.Render();
        }
        finally
        {
            // Even a throwing partial draw needs an explicit completion receipt. The slot keeps
            // this retain when no fence can be inserted and quarantines future refreshes.
            _slot.CompleteGpuReaderAfterRender();
        }
    }
}
