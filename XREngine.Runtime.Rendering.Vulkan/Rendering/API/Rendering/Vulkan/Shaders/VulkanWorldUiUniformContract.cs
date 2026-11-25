using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>Checks reflected world UI members and their exact native bytes.</summary>
internal static class VulkanWorldUiUniformContract
{
    internal static bool TryFindMember(
        ReadOnlySpan<AutoUniformBlockInfo> blocks, string name,
        EShaderType shaderType, EShaderVarType expectedType,
        out AutoUniformBlockInfo? foundBlock,
        out AutoUniformMember foundMember)
    {
        foundBlock = null;
        foundMember = default;
        if (expectedType is not (EShaderVarType._vec4 or EShaderVarType._mat4))
            return false;
        for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
        {
            AutoUniformBlockInfo block = blocks[blockIndex];
            if (block.ShaderType != shaderType)
                continue;
            for (int memberIndex = 0; memberIndex < block.Members.Count;
                 memberIndex++)
            {
                AutoUniformMember member = block.Members[memberIndex];
                if (!string.Equals(member.Name, name, StringComparison.Ordinal))
                    continue;
                uint size = expectedType == EShaderVarType._vec4 ? 16u : 64u;
                if (foundBlock is not null || member.EngineType != expectedType ||
                    !string.Equals(member.GlslType,
                        expectedType == EShaderVarType._vec4 ? "vec4" : "mat4",
                        StringComparison.Ordinal) ||
                    member.IsArray || member.Size != size ||
                    member.Offset > block.Size || size > block.Size - member.Offset)
                    return false;
                foundBlock = block;
                foundMember = member;
            }
        }
        return foundBlock is not null;
    }

    internal static bool BytesMatch<T>(ReadOnlySpan<byte> bytes,
        uint offset, in T expected) where T : unmanaged
    {
        int length = Unsafe.SizeOf<T>();
        if (offset > bytes.Length || length > bytes.Length - offset)
            return false;
        ReadOnlySpan<T> value = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.AsRef(in expected), 1);
        return bytes.Slice((int)offset, length).SequenceEqual(
            MemoryMarshal.AsBytes(value));
    }
}
