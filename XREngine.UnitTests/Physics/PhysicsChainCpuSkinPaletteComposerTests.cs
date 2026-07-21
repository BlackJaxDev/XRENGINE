using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainCpuSkinPaletteComposerTests
{
    [Test]
    public void PackedMatrixMatchesRendererCompositionOrderAndSize()
    {
        Matrix4x4 rootBind = Matrix4x4.CreateTranslation(1.0f, 0.0f, 0.0f);
        Matrix4x4[] inverseBind = [Matrix4x4.CreateTranslation(0.0f, 2.0f, 0.0f)];
        Matrix4x4[] world = [Matrix4x4.CreateTranslation(0.0f, 0.0f, 3.0f)];
        PhysicsChainCpuSkinPaletteMapping[] mappings = [new(0, 0)];
        Span<PhysicsChainCpuSkinPaletteMatrix> destination = stackalloc PhysicsChainCpuSkinPaletteMatrix[1];

        PhysicsChainCpuSkinPaletteComposer.TryCompose(world, inverseBind, mappings, rootBind, destination).ShouldBeTrue();

        destination[0].Row0.ShouldBe(new Vector4(1.0f, 0.0f, 0.0f, 1.0f));
        destination[0].Row1.ShouldBe(new Vector4(0.0f, 1.0f, 0.0f, 2.0f));
        destination[0].Row2.ShouldBe(new Vector4(0.0f, 0.0f, 1.0f, 3.0f));
        Marshal.SizeOf<PhysicsChainCpuSkinPaletteMatrix>().ShouldBe(48);
    }

    [Test]
    public void CurrentAndPreviousRemainIndependentForMotionVectors()
    {
        Matrix4x4[] current = [Matrix4x4.CreateRotationX(0.5f)];
        Matrix4x4[] previous = [Matrix4x4.Identity];
        Matrix4x4[] inverseBind = [Matrix4x4.Identity];
        PhysicsChainCpuSkinPaletteMapping[] mappings = [new(0, 0)];
        Span<PhysicsChainCpuSkinPaletteMatrix> currentOutput = stackalloc PhysicsChainCpuSkinPaletteMatrix[1];
        Span<PhysicsChainCpuSkinPaletteMatrix> previousOutput = stackalloc PhysicsChainCpuSkinPaletteMatrix[1];

        PhysicsChainCpuSkinPaletteComposer.TryComposeCurrentAndPrevious(
            current,
            previous,
            inverseBind,
            mappings,
            Matrix4x4.Identity,
            currentOutput,
            previousOutput).ShouldBeTrue();

        currentOutput[0].ShouldNotBe(previousOutput[0]);
        previousOutput[0].ShouldBe(PhysicsChainCpuSkinPaletteMatrix.FromRowVectorMatrix(Matrix4x4.Identity));
    }

    [Test]
    public void InvalidMappingFailsBeforeWritingAnyElement()
    {
        Matrix4x4[] matrices = [Matrix4x4.Identity];
        PhysicsChainCpuSkinPaletteMapping[] mappings = [new(0, 0), new(8, 0)];
        Span<PhysicsChainCpuSkinPaletteMatrix> destination = stackalloc PhysicsChainCpuSkinPaletteMatrix[1];
        destination[0] = new PhysicsChainCpuSkinPaletteMatrix(Vector4.One, Vector4.One, Vector4.One);

        PhysicsChainCpuSkinPaletteComposer.TryCompose(
            matrices,
            matrices,
            mappings,
            Matrix4x4.Identity,
            destination).ShouldBeFalse();

        destination[0].ShouldBe(new PhysicsChainCpuSkinPaletteMatrix(Vector4.One, Vector4.One, Vector4.One));
    }
}
