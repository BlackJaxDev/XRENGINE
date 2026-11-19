using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    private sealed partial class RendererResources
    {
        /// <summary>Keeps one complete compute output generation alive across GPU submissions.</summary>
        private sealed class OutputSlot(XRMeshRenderer.SkinnedOutputResourceState state)
        {
            public XRMeshRenderer.SkinnedOutputResourceState State { get; } = state;
            public bool HasBeenSelected { get; set; }

            public bool IsReadyForOverwrite(IGpuBufferContentReuseCapability capability)
                => IsReady(capability, State.Positions) &&
                   IsReady(capability, State.Normals) &&
                   IsReady(capability, State.Tangents) &&
                   IsReady(capability, State.Interleaved);

            private static bool IsReady(IGpuBufferContentReuseCapability capability, XRDataBuffer? buffer)
                => buffer is null || capability.QueryBufferContentReuse(buffer) == EGpuBufferContentReuseStatus.Ready;

            public void Destroy()
            {
                State.Positions?.Destroy();
                State.Normals?.Destroy();
                State.Tangents?.Destroy();
                State.Interleaved?.Destroy();
            }
        }
    }
}
