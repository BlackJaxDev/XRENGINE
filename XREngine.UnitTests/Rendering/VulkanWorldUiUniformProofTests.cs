using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanWorldUiUniformProofTests
{
    [Test]
    public void ReflectedCropMemberNeedsOneExactTypeAndRange()
    {
        AutoUniformMember crop = Member("UiWorldCropMinMax",
            "vec4", EShaderVarType._vec4, 16, 16);
        AutoUniformBlockInfo block = Block(crop);
        Assert.That(VulkanWorldUiUniformContract.TryFindMember([block],
            crop.Name, EShaderType.Fragment, EShaderVarType._vec4,
            out AutoUniformBlockInfo? found, out AutoUniformMember member),
            Is.True);
        Assert.That(found, Is.SameAs(block));
        Assert.That(member.Offset, Is.EqualTo(16));

        Assert.That(VulkanWorldUiUniformContract.TryFindMember([block, Block(crop)],
            crop.Name, EShaderType.Fragment, EShaderVarType._vec4,
            out _, out _), Is.False);
        Assert.That(VulkanWorldUiUniformContract.TryFindMember(
            [Block(crop with { EngineType = EShaderVarType._vec3 })],
            crop.Name, EShaderType.Fragment, EShaderVarType._vec4,
            out _, out _), Is.False);
        Assert.That(VulkanWorldUiUniformContract.TryFindMember(
            [Block(crop with { Offset = 48 })],
            crop.Name, EShaderType.Fragment, EShaderVarType._vec4,
            out _, out _), Is.False);
    }

    [Test]
    public void NativeCropBytesMustMatchAtReflectedOffset()
    {
        Vector4 frozen = new(1, 2, 30, 40);
        byte[] bytes = new byte[80];
        MemoryMarshal.Write(bytes.AsSpan(16), in frozen);
        Assert.That(VulkanWorldUiUniformContract.BytesMatch(bytes, 16u,
            in frozen), Is.True);
        Assert.That(VulkanWorldUiUniformContract.BytesMatch(bytes, 0u,
            in frozen), Is.False);
        Assert.That(VulkanWorldUiUniformContract.BytesMatch(bytes, 70u,
            in frozen), Is.False);
        bytes[16] ^= 1;
        Assert.That(VulkanWorldUiUniformContract.BytesMatch(bytes, 16u,
            in frozen), Is.False);
    }

    [Test]
    public void NativeModelBytesNeedTheFrozenMatrix()
    {
        Matrix4x4 frozen = Matrix4x4.CreateTranslation(3, 4, 5);
        byte[] bytes = new byte[96];
        MemoryMarshal.Write(bytes.AsSpan(16), in frozen);
        Assert.That(VulkanWorldUiUniformContract.BytesMatch(bytes, 16u,
            in frozen), Is.True);
        Matrix4x4 newer = Matrix4x4.CreateTranslation(4, 4, 5);
        Assert.That(VulkanWorldUiUniformContract.BytesMatch(bytes, 16u,
            in newer), Is.False);
    }

    private static AutoUniformMember Member(string name, string glslType,
        EShaderVarType type, uint offset, uint size)
        => new(name, glslType, type, false, 0, 0, offset, size,
            null, null);

    private static AutoUniformBlockInfo Block(AutoUniformMember member)
        => new("WorldUi", "WorldUi_Instance", 0, 3, 48,
            [member], EShaderType.Fragment);
}
