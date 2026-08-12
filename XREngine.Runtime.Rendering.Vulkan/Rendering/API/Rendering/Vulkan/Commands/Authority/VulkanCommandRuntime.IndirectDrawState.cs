using System.Numerics;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    internal bool TryBeginIndirectDrawState(
        XRRenderProgram program,
        XRMaterial? material,
        in Matrix4x4 modelMatrix,
        out IndirectDrawStateToken token)
    {
        token = CommandBuffers.PendingIndirectDrawState is { } previous
            ? new(previous.Program, previous.Material, previous.ModelMatrix, true)
            : default;

        if (material is null)
        {
            Debug.RenderingWarningEvery(
                "RenderDispatch.VulkanIndirectDrawStateMissingMaterial",
                TimeSpan.FromSeconds(2),
                "[RenderDispatch] Vulkan indirect draw skipped because no material was provided for captured draw state.");
            return false;
        }

        CommandBuffers.PendingIndirectDrawState = new(program, material, modelMatrix);
        return true;
    }

    internal void EndIndirectDrawState(in IndirectDrawStateToken token)
        => CommandBuffers.PendingIndirectDrawState = token.HadPreviousState
            ? new(token.PreviousProgram!, token.PreviousMaterial!, token.PreviousModelMatrix)
            : null;
}
