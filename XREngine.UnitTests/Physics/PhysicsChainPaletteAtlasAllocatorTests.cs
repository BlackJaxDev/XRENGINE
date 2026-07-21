using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

public sealed class PhysicsChainPaletteAtlasAllocatorTests
{
    [Test]
    public void ExistingSlicesRemainStableWhenOtherConsumersComeAndGo()
    {
        var allocator = new PhysicsChainPaletteAtlasAllocator();
        object component = new();
        var retainedKey = new PhysicsChainPaletteSliceKey(component, new object());
        var transientKey = new PhysicsChainPaletteSliceKey(component, new object());

        allocator.BeginLayout();
        PhysicsChainPaletteSlice retained = allocator.Acquire(retainedKey, 12u);
        allocator.Acquire(transientKey, 7u);
        allocator.EndLayout();

        allocator.BeginLayout();
        PhysicsChainPaletteSlice retainedNextFrame = allocator.Acquire(retainedKey, 12u);
        allocator.EndLayout();

        retainedNextFrame.BaseElement.ShouldBe(retained.BaseElement);
        retainedNextFrame.RequiresHistoryReset.ShouldBeFalse();
        allocator.LiveSliceCount.ShouldBe(1);
    }

    [Test]
    public void ReleasedSliceIsReusedAndRequiresBothHistoriesToReset()
    {
        var allocator = new PhysicsChainPaletteAtlasAllocator();
        object component = new();
        var oldKey = new PhysicsChainPaletteSliceKey(component, new object());
        var newKey = new PhysicsChainPaletteSliceKey(component, new object());

        allocator.BeginLayout();
        PhysicsChainPaletteSlice oldSlice = allocator.Acquire(oldKey, 9u);
        allocator.EndLayout();

        allocator.BeginLayout();
        allocator.EndLayout();

        allocator.BeginLayout();
        PhysicsChainPaletteSlice reused = allocator.Acquire(newKey, 9u);
        allocator.EndLayout();

        reused.BaseElement.ShouldBe(oldSlice.BaseElement);
        reused.RequiresHistoryReset.ShouldBeTrue();
    }

    [Test]
    public void CompatibleRenderersCanShareOneSliceKey()
    {
        var allocator = new PhysicsChainPaletteAtlasAllocator();
        object component = new();
        object compatibleMesh = new();
        var sharedKey = new PhysicsChainPaletteSliceKey(component, compatibleMesh);

        allocator.BeginLayout();
        PhysicsChainPaletteSlice firstRenderer = allocator.Acquire(sharedKey, 24u);
        PhysicsChainPaletteSlice secondRenderer = allocator.Acquire(sharedKey, 24u);
        allocator.EndLayout();

        secondRenderer.BaseElement.ShouldBe(firstRenderer.BaseElement);
        allocator.LiveSliceCount.ShouldBe(1);
        allocator.HighWater.ShouldBe(24u);
    }
}
