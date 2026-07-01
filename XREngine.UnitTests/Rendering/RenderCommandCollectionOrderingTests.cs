using NUnit.Framework;
using Shouldly;
using System.Collections.Generic;
using System.IO;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderCommandCollectionOrderingTests
{
    private IRuntimeShaderServices? _previousShaderServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousShaderServices;

    [Test]
    public void Equal2DCommands_PreserveSubmissionOrder_InTransparentPass()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?>
        {
            [pass] = new FarToNearRenderCommandSorter()
        });
        List<string> rendered = [];

        commands.AddCPU(new TestRenderCommand(pass, 0, "first", rendered));
        commands.AddCPU(new TestRenderCommand(pass, 0, "second", rendered));
        commands.AddCPU(new TestRenderCommand(pass, 0, "third", rendered));

        commands.SwapBuffers();
        commands.RenderCPU(pass);

        rendered.ShouldBe(["first", "second", "third"]);
    }

    [Test]
    public void Equal2DCommands_PreserveSubmissionOrder_InOpaquePass()
    {
        const int pass = (int)EDefaultRenderPass.OpaqueForward;
        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?>
        {
            [pass] = new NearToFarRenderCommandSorter()
        });
        List<string> rendered = [];

        commands.AddCPU(new TestRenderCommand(pass, 0, "first", rendered));
        commands.AddCPU(new TestRenderCommand(pass, 0, "second", rendered));
        commands.AddCPU(new TestRenderCommand(pass, 0, "third", rendered));

        commands.SwapBuffers();
        commands.RenderCPU(pass);

        rendered.ShouldBe(["first", "second", "third"]);
    }

    [Test]
    public void Sorted3DPass_CapturesSortKeys_WhenSharedCommandDistanceMutates()
    {
        const int pass = (int)EDefaultRenderPass.OpaqueDeferred;
        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?>
        {
            [pass] = new NearToFarRenderCommandSorter()
        });
        List<string> rendered = [];

        TestRenderCommand3D far = new(pass, 100.0f, "far", rendered);
        TestRenderCommand3D near = new(pass, 1.0f, "near", rendered);
        far.SetInitialDistance();
        near.SetInitialDistance();

        commands.AddCPU(far);
        commands.AddCPU(near);
        commands.SwapBuffers();

        far.RenderDistance = 0.0f;
        near.RenderDistance = 200.0f;

        commands.RenderCPU(pass);

        rendered.ShouldBe(["near", "far"]);
    }

    [Test]
    public void SortedRenderPasses_UseSnapshotCollections_NotLiveMutableSortedSet()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs");
        string commandSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommand.cs");

        source.ShouldContain("SnapshotSortedRenderCommandCollection");
        source.ShouldContain("SnapshotSortedRenderCommandCollection : ICollection<RenderCommand>, IReadOnlyCollection<RenderCommand>");
        source.ShouldContain("Entry.Capture(item, sortOrderKey)");
        source.ShouldContain("snapshotSet.Add(item, sortOrderKey)");
        source.ShouldNotContain("_entries.Add(Entry.Capture(item));");
        source.ShouldNotContain("new SortedSet<RenderCommand>");
        source.ShouldContain("hostServices.RenderWindowsWhileInVR && hostServices.VrMirrorComposeFromEyeTextures");
        commandSource.ShouldNotContain("_swapQueued");
        source.ShouldContain("_updatingSwapQueueMembership");
        source.ShouldContain("public bool IsRenderCommandSnapshotAuthority");
        source.ShouldContain("if (IsRenderCommandSnapshotAuthority)");
        commandSource.ShouldContain("RenderCommandCollection.IsRenderCommandSnapshotAuthority=false");
    }

    [Test]
    public void SortedRenderPasses_AreExposedThroughRenderingPassCommandLookup()
    {
        const int pass = (int)EDefaultRenderPass.OpaqueDeferred;
        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?>
        {
            [pass] = new NearToFarRenderCommandSorter()
        });
        List<string> rendered = [];
        TestRenderCommand3D far = new(pass, 100.0f, "far", rendered);
        TestRenderCommand3D near = new(pass, 1.0f, "near", rendered);
        far.SetInitialDistance();
        near.SetInitialDistance();

        commands.AddCPU(far);
        commands.AddCPU(near);
        commands.SwapBuffers();

        bool found = commands.TryGetRenderingPassCommands(pass, out IReadOnlyCollection<RenderCommand>? passCommands);

        found.ShouldBeTrue();
        passCommands.ShouldNotBeNull();
        passCommands.Count.ShouldBe(2);
        passCommands.ToArray().ShouldBe(new RenderCommand[] { near, far });
    }

    [Test]
    public void AddCpu_LooksUpUpdatingPassAfterCollectionLockIsHeld()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs");
        string method = SliceBetween(source, "public void AddCPU(RenderCommand item)", "private long GetSortOrderKey");

        int lockIndex = method.IndexOf("using (_lock.EnterScope())", StringComparison.Ordinal);
        int lookupIndex = method.IndexOf("_updatingPasses.TryGetValue(pass, out var set)", StringComparison.Ordinal);

        lockIndex.ShouldBeGreaterThanOrEqualTo(0);
        lookupIndex.ShouldBeGreaterThan(lockIndex);
    }

    [Test]
    public void DirtyPublishQueue_IsCollectionLocal_ForSharedCommands()
    {
        const int pass = (int)EDefaultRenderPass.OpaqueDeferred;
        Dictionary<int, IComparer<RenderCommand>?> passSorters = new()
        {
            [pass] = new NearToFarRenderCommandSorter()
        };

        RenderCommandCollection firstCollector = new(passSorters);
        RenderCommandCollection secondCollector = new(passSorters);
        List<string> rendered = [];
        TestPublishingCommand sharedCommand = new(pass, "shared", rendered);

        firstCollector.AddCPU(sharedCommand);
        secondCollector.AddCPU(sharedCommand);

        secondCollector.SwapBuffers();

        sharedCommand.PublishCount.ShouldBe(1);
        sharedCommand.HasPublished.ShouldBeTrue();

        firstCollector.SwapBuffers();
        sharedCommand.PublishCount.ShouldBe(1);
    }

    [Test]
    public void NonAuthoritativeCollection_DoesNotClaimSharedCommandSnapshotWhenAuthorityQueued()
    {
        const int pass = (int)EDefaultRenderPass.OpaqueDeferred;
        Dictionary<int, IComparer<RenderCommand>?> passSorters = new()
        {
            [pass] = new NearToFarRenderCommandSorter()
        };

        RenderCommandCollection eyeCollector = new(passSorters)
        {
            IsRenderCommandSnapshotAuthority = false
        };
        RenderCommandCollection desktopCollector = new(passSorters);
        List<string> rendered = [];
        TestPublishingCommand sharedCommand = new(pass, "shared", rendered);

        eyeCollector.AddCPU(sharedCommand);
        desktopCollector.AddCPU(sharedCommand);

        eyeCollector.SwapBuffers();

        sharedCommand.PublishCount.ShouldBe(0);
        sharedCommand.HasPublished.ShouldBeFalse();

        desktopCollector.SwapBuffers();

        sharedCommand.PublishCount.ShouldBe(1);
        sharedCommand.HasPublished.ShouldBeTrue();
    }

    [Test]
    public void NonAuthoritativeCollection_PublishesCommandWhenNoAuthorityQueued()
    {
        const int pass = (int)EDefaultRenderPass.OpaqueDeferred;
        Dictionary<int, IComparer<RenderCommand>?> passSorters = new()
        {
            [pass] = new NearToFarRenderCommandSorter()
        };

        RenderCommandCollection eyeCollector = new(passSorters)
        {
            IsRenderCommandSnapshotAuthority = false
        };
        List<string> rendered = [];
        TestPublishingCommand eyeOnlyCommand = new(pass, "eye-only", rendered);

        eyeCollector.AddCPU(eyeOnlyCommand);
        eyeCollector.SwapBuffers();

        eyeOnlyCommand.PublishCount.ShouldBe(1);
        eyeOnlyCommand.HasPublished.ShouldBeTrue();
    }

    [Test]
    public void VisualScene2D_NoCullingVolume_PreservesRenderableInsertionOrder()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;

        VisualScene2D scene = new();
        scene.SetBounds(new BoundingRectangleF(0.0f, 0.0f, 100.0f, 100.0f));

        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?>
        {
            [pass] = new FarToNearRenderCommandSorter()
        });
        List<string> rendered = [];

        TestRenderable topRight = new("top-right", pass, rendered, new BoundingRectangleF(75.0f, 75.0f, 5.0f, 5.0f));
        TestRenderable bottomLeft = new("bottom-left", pass, rendered, new BoundingRectangleF(5.0f, 5.0f, 5.0f, 5.0f));
        TestRenderable topLeft = new("top-left", pass, rendered, new BoundingRectangleF(5.0f, 75.0f, 5.0f, 5.0f));

        scene.AddRenderable(topRight.Info);
        scene.AddRenderable(bottomLeft.Info);
        scene.AddRenderable(topLeft.Info);

        scene.CollectRenderedItems(commands, (BoundingRectangleF?)null, null);
        commands.SwapBuffers();
        commands.RenderCPU(pass);

        rendered.ShouldBe(["top-right", "bottom-left", "top-left"]);
    }

    private sealed class TestRenderCommand(int renderPass, int zIndex, string name, List<string> rendered)
        : RenderCommand2D(renderPass, zIndex)
    {
        public override void Render() => rendered.Add(name);
    }

    private sealed class TestPublishingCommand(int renderPass, string name, List<string> rendered)
        : RenderCommand2D(renderPass, zIndex: 0)
    {
        public int PublishCount { get; private set; }
        public bool HasPublished { get; private set; }

        public override void Render() => rendered.Add(name);

        public override void SwapBuffers()
        {
            PublishCount++;
            HasPublished = true;
            base.SwapBuffers();
        }
    }

    private sealed class TestRenderCommand3D(int renderPass, float renderDistance, string name, List<string> rendered)
        : RenderCommand3D(renderPass)
    {
        public override void Render() => rendered.Add(name);

        public void SetInitialDistance()
            => RenderDistance = renderDistance;

        public override void CollectedForRender(IRuntimeRenderCamera? camera)
        {
            base.CollectedForRender(camera);
            RenderDistance = renderDistance;
        }
    }

    private sealed class TestRenderable : IRenderable
    {
        public TestRenderable(string name, int renderPass, List<string> rendered, BoundingRectangleF bounds)
        {
            Info = RenderInfo2D.New(this, new TestRenderCommand(renderPass, 0, name, rendered));
            Info.CullingVolume = bounds;
            RenderedObjects = [Info];
        }

        public RenderInfo2D Info { get; }
        public RenderInfo[] RenderedObjects { get; }
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string root = ResolveWorkspaceRoot();
        string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(fullPath).ShouldBeTrue($"Expected workspace file to exist: {relativePath}");
        return File.ReadAllText(fullPath).Replace("\r\n", "\n");
    }

    private static string SliceBetween(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Expected to find start token '{startToken}'.");

        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Expected to find end token '{endToken}' after '{startToken}'.");

        return source[start..end];
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? dir = new(TestContext.CurrentContext.TestDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XRENGINE.sln")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find workspace root from test directory '{TestContext.CurrentContext.TestDirectory}'.");
    }
}
