using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    /// <summary>Checks the post-callback raster image before a separate producer may reject geometry using frozen transforms.</summary>
    internal bool MatchesAuthoredInstanceMatrix(string name, in Matrix4x4 expected)
    {
        if (!_uniforms.TryGetValue(name, out List<UniformTarget>? targets) || targets.Count == 0) return false;
        foreach (UniformTarget target in targets)
        {
            if (target.Member.PhysicalType != "mat4x4<f32>" || target.Member.Size != 64 ||
                MemoryMarshal.Read<Matrix4x4>(target.Block.Bytes.AsSpan(checked((int)target.Member.Offset), 64)) != expected)
                return false;
        }
        return true;
    }
}
