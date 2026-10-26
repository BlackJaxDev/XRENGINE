using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GaussianBakeBudgetTests
{
    [TestCase(GaussianBakeKind.Avatar)]
    [TestCase(GaussianBakeKind.Item)]
    public void For_AvatarOrItem_ReturnsPrefabRow(GaussianBakeKind kind)
        => GaussianBakeBudget.For(kind).ShouldBe(new GaussianBakeBudget(
            MaxSplats: 50_000,
            MaxDriverVertices: 8_000,
            MaxDurationSeconds: 10f,
            FrameRate: 30f,
            Chunk0Bytes: 262_144,
            MaxCacheBytes: 16_777_216L,
            MaxPositionErrorMeters: 0.0005f,
            L0TargetSplats: 2_000));

    [Test]
    public void For_World_ReturnsWorldRow()
        => GaussianBakeBudget.For(GaussianBakeKind.World).ShouldBe(new GaussianBakeBudget(
            MaxSplats: 300_000,
            MaxDriverVertices: 16_000,
            MaxDurationSeconds: 10f,
            FrameRate: 30f,
            Chunk0Bytes: 1_048_576,
            MaxCacheBytes: 50_331_648L,
            MaxPositionErrorMeters: 0.002f,
            L0TargetSplats: 5_000));

    [Test]
    public void For_UndefinedKind_Throws()
        => Should.Throw<ArgumentOutOfRangeException>(() => GaussianBakeBudget.For((GaussianBakeKind)99));
}
