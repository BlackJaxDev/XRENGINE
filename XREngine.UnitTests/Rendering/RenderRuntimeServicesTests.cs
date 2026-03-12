using System.Collections.Concurrent;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderRuntimeServicesTests
{
    private IRuntimeRenderObjectServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeRenderObjectServices.Current;
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeRenderObjectServices.Current = _previousServices;
    }

    [Test]
    public void DispatchCompute_WithBarrier_UsesRuntimeRenderServices()
    {
        TestRenderServices services = new();
        RuntimeRenderObjectServices.Current = services;

        XRRenderProgram program = new();
        bool dispatched = false;
        program.DispatchComputeRequested += (x, y, z, textures) =>
        {
            dispatched = true;
            x.ShouldBe(2u);
            y.ShouldBe(3u);
            z.ShouldBe(4u);
            textures.ShouldBeNull();
        };

        program.DispatchCompute(2u, 3u, 4u, EMemoryBarrierMask.ShaderStorage);

        dispatched.ShouldBeTrue();
        services.LastBarrierMask.ShouldBe(EMemoryBarrierMask.ShaderStorage);
    }

    [Test]
    public void Print_UsesRuntimeRenderServicesOutput()
    {
        TestRenderServices services = new();
        RuntimeRenderObjectServices.Current = services;

        XRDataBuffer buffer = new(EBufferTarget.ArrayBuffer, integral: false);
        buffer.SetDataRaw(new List<float> { 1.0f, 2.0f }, remap: false);

        buffer.Print();

        services.OutputMessages.Count.ShouldBe(1);
        services.OutputMessages[0].ShouldContain("1");
        services.OutputMessages[0].ShouldContain("2");
    }

    private sealed class TestRenderServices : IRuntimeRenderObjectServices
    {
        public EMemoryBarrierMask? LastBarrierMask { get; private set; }
        public List<string> OutputMessages { get; } = [];

        public AbstractRenderAPIObject?[] CreateObjectsForAllOwners(GenericRenderObject renderObject)
            => [];

        public ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> CreateObjectsForOwner(IRenderApiWrapperOwner owner)
            => [];

        public void DestroyObjectsForOwner(IRenderApiWrapperOwner owner)
        {
        }

        public void IssueMemoryBarrier(EMemoryBarrierMask mask)
            => LastBarrierMask = mask;

        public void LogOutput(string message)
            => OutputMessages.Add(message);

        public void LogWarning(string message)
        {
        }
    }
}
