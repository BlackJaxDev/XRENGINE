using System.Text;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuMeshDraw
{
    /// <summary>Describes the retained draw only when preparation diagnostics are requested.</summary>
    internal void AppendPreparationStatus(StringBuilder output)
        => output.Append(" owner=mesh draw program=").Append(_program.Artifact.Name)
            .Append(" target=").Append(_frameBuffer is null ? "canvas" : _frameBuffer.Data.Name ?? "unnamed framebuffer")
            .Append(" pipeline=").Append(_preparation.Status).Append(" disposed=").Append(_disposed);
}
