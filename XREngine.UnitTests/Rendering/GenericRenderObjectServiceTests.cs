using System.Collections.Concurrent;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GenericRenderObjectServiceTests
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
    public void Constructor_AttachesWrappersFromRuntimeServices()
    {
        TestOwner ownerA = new("OwnerA");
        TestOwner ownerB = new("OwnerB");
        TestRenderObjectServices services = new(ownerA, ownerB);
        RuntimeRenderObjectServices.Current = services;

        TestRenderObject renderObject = new();

        services.CreateForAllOwnersCallCount.ShouldBe(1);
        renderObject.APIWrappers.Count.ShouldBe(2);
        renderObject.APIWrappers.ShouldAllBe(wrapper => wrapper is TestApiObject);
        renderObject.APIWrappers.Select(wrapper => wrapper.Owner.RenderApiWrapperOwnerName).OrderBy(name => name)
            .ShouldBe(["OwnerA", "OwnerB"]);
    }

    [Test]
    public void Generate_PropagatesToAttachedWrappers()
    {
        TestOwner owner = new("OwnerA");
        TestRenderObjectServices services = new(owner);
        RuntimeRenderObjectServices.Current = services;

        TestRenderObject renderObject = new();

        renderObject.Generate();

        owner.CreatedWrappers.Count.ShouldBe(1);
        owner.CreatedWrappers[0].GenerateCallCount.ShouldBe(1);
    }

    private sealed class TestRenderObject : GenericRenderObject
    {
    }

    private sealed class TestApiObject : AbstractRenderAPIObject
    {
        public TestApiObject(IRenderApiWrapperOwner owner)
            : base(owner)
        {
        }

        public int GenerateCallCount { get; private set; }

        public override bool IsGenerated => GenerateCallCount > 0;

        public override void Generate()
            => GenerateCallCount++;

        public override void Destroy()
        {
        }

        public override string GetDescribingName()
            => "TestApiObject";
    }

    private sealed class TestOwner(string name) : IRenderApiWrapperOwner
    {
        public string RenderApiWrapperOwnerName => name;

        public List<TestApiObject> CreatedWrappers { get; } = [];

        public AbstractRenderAPIObject? GetOrCreateAPIRenderObject(GenericRenderObject renderObject, bool generateNow = false)
        {
            TestApiObject wrapper = new(this);
            if (generateNow)
                wrapper.Generate();

            CreatedWrappers.Add(wrapper);
            return wrapper;
        }
    }

    private sealed class TestRenderObjectServices(params TestOwner[] owners) : IRuntimeRenderObjectServices
    {
        public int CreateForAllOwnersCallCount { get; private set; }

        public AbstractRenderAPIObject?[] CreateObjectsForAllOwners(GenericRenderObject renderObject)
        {
            CreateForAllOwnersCallCount++;
            return [.. owners.Select(owner => owner.GetOrCreateAPIRenderObject(renderObject))];
        }

        public ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> CreateObjectsForOwner(IRenderApiWrapperOwner owner)
            => [];

        public void DestroyObjectsForOwner(IRenderApiWrapperOwner owner)
        {
        }

        public void IssueMemoryBarrier(EMemoryBarrierMask mask)
        {
        }

        public void LogOutput(string message)
        {
        }

        public void LogWarning(string message)
        {
        }
    }
}
