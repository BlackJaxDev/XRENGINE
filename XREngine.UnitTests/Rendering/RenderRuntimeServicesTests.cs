using System.Collections.Concurrent;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data;
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

    [Test]
    public void EnsureRawCapacity_GrowsGeometrically()
    {
        XRDataBuffer buffer = new(EBufferTarget.ArrayBuffer, integral: false);

        buffer.EnsureRawCapacity<float>(3).ShouldBeTrue();
        buffer.ElementCount.ShouldBe(4u);

        buffer.EnsureRawCapacity<float>(4).ShouldBeFalse();
        buffer.ElementCount.ShouldBe(4u);

        buffer.EnsureRawCapacity<float>(5).ShouldBeTrue();
        buffer.ElementCount.ShouldBe(8u);
    }

    [Test]
    public void WriteDataRaw_ReusesExistingClientSourceForSmallerUpdates()
    {
        XRDataBuffer buffer = new(EBufferTarget.ArrayBuffer, integral: false);
        buffer.EnsureRawCapacity<float>(8).ShouldBeTrue();

        DataSource? initialSource = buffer.ClientSideSource;
        initialSource.ShouldNotBeNull();

        uint byteLength = buffer.WriteDataRaw<float>(new float[] { 1.0f, 2.0f, 3.0f });

        byteLength.ShouldBe((uint)(3 * sizeof(float)));
        buffer.ClientSideSource.ShouldBeSameAs(initialSource);
        buffer.ElementCount.ShouldBe(8u);
        buffer.GetFloat(0).ShouldBe(1.0f);
        buffer.GetFloat(1).ShouldBe(2.0f);
        buffer.GetFloat(2).ShouldBe(3.0f);
    }

    [Test]
    public void WriteDataRaw_WithElementOffset_UpdatesOnlyRequestedRange()
    {
        XRDataBuffer buffer = new(EBufferTarget.ArrayBuffer, integral: false);
        buffer.EnsureRawCapacity<float>(6).ShouldBeTrue();
        buffer.WriteDataRaw<float>(new float[] { 1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f });

        uint byteLength = buffer.WriteDataRaw<float>(new float[] { 10.0f, 20.0f }, 2u);

        byteLength.ShouldBe((uint)(2 * sizeof(float)));
        buffer.GetFloat(0).ShouldBe(1.0f);
        buffer.GetFloat(1).ShouldBe(2.0f);
        buffer.GetFloat(2).ShouldBe(10.0f);
        buffer.GetFloat(3).ShouldBe(20.0f);
        buffer.GetFloat(4).ShouldBe(5.0f);
        buffer.GetFloat(5).ShouldBe(6.0f);
    }

    [Test]
    public void SetDataRaw_ListOfBlittableStructs_RoundTripsThroughContiguousCopyPath()
    {
        XRDataBuffer buffer = new(EBufferTarget.ArrayBuffer, integral: false);
        List<Vector4> values =
        [
            new Vector4(1.0f, 2.0f, 3.0f, 4.0f),
            new Vector4(5.0f, 6.0f, 7.0f, 8.0f),
        ];

        buffer.SetDataRaw(values, remap: false).ShouldBeNull();

        Vector4[] roundTrip = buffer.GetDataArrayRawAtIndex<Vector4>(0u, values.Count);
        roundTrip.ShouldBe(values.ToArray());
    }

    [Test]
    public void SetDataArrayRawAtIndex_BlittableStructs_UpdatesContiguousRange()
    {
        XRDataBuffer buffer = new(EBufferTarget.ArrayBuffer, integral: false);
        buffer.Allocate<Vector4>(4u);

        Vector4[] values =
        [
            new Vector4(9.0f, 8.0f, 7.0f, 6.0f),
            new Vector4(5.0f, 4.0f, 3.0f, 2.0f),
        ];

        buffer.SetDataArrayRawAtIndex(1u, values);

        buffer.GetDataRawAtIndex<Vector4>(0u).ShouldBe(Vector4.Zero);
        buffer.GetDataArrayRawAtIndex<Vector4>(1u, values.Length).ShouldBe(values);
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
