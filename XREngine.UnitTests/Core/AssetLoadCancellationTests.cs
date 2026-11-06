using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XREngine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using TestContext = NUnit.Framework.TestContext;

namespace XREngine.UnitTests.Core;

/// <summary>
/// Tests the cancelable <see cref="AssetManager.LoadAsync{T}(string, JobPriority, bool, CancellationToken)"/> overload.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class AssetLoadCancellationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private string _rootPath = string.Empty;
    private string _cachePath = string.Empty;
    private string _sourcePath = string.Empty;
    private AssetManager _manager = null!;
    private IDisposable? _factory;
    private IDisposable? _nestingFactory;
    private IDisposable? _innerFactory;

    [SetUp]
    public void SetUp()
    {
        _rootPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "AssetLoadCancellation", Guid.NewGuid().ToString("N"));
        string assetsPath = Path.Combine(_rootPath, "Assets");
        _cachePath = Path.Combine(_rootPath, "Cache");
        Directory.CreateDirectory(assetsPath);
        Directory.CreateDirectory(_cachePath);

        _sourcePath = Path.GetFullPath(Path.Combine(assetsPath, "sample.cancelstub"));
        File.WriteAllText(_sourcePath, "payload");

        _factory = RuntimeCookedBinarySerializer.RegisterRuntimeFactory<BlockingStubAsset>(static () => new BlockingStubAsset());
        _nestingFactory = RuntimeCookedBinarySerializer.RegisterRuntimeFactory<NestingStubAsset>(static () => new NestingStubAsset());
        _innerFactory = RuntimeCookedBinarySerializer.RegisterRuntimeFactory<InnerStubAsset>(static () => new InnerStubAsset());
        _manager = new AssetManager
        {
            MonitorGameAssetsForChanges = false,
            GameAssetsPath = assetsPath,
            GameCachePath = _cachePath,
        };
        BlockingStubAsset.Reset();
    }

    [TearDown]
    public void TearDown()
    {
        BlockingStubAsset.Release.Set();
        _manager.Dispose();
        _factory?.Dispose();
        _nestingFactory?.Dispose();
        _innerFactory?.Dispose();
        NestingStubAsset.Manager = null;
        XRObjectBase.ProcessPendingDestructions();
        try
        {
            if (Directory.Exists(_rootPath))
                Directory.Delete(_rootPath, recursive: true);
        }
        catch
        {
            // Cleanup is best effort.
        }
    }

    [Test]
    public async Task LoadAsync_TokenCanceledBeforeStart_ThrowsAndCachesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _manager.LoadAsync<BlockingStubAsset>(_sourcePath, JobPriority.Normal, bypassJobThread: true, cts.Token));

        BlockingStubAsset.LoadCount.ShouldBe(0);
        _manager.TryGetAssetByPath(_sourcePath, out _).ShouldBeFalse();
    }

    [Test]
    public async Task LoadAsync_CanceledDuringLoad_DiscardsAssetReleasesGateAndLaterLoadSucceeds()
    {
        using var cts = new CancellationTokenSource();
        Task<BlockingStubAsset?> canceledLoad = StartLoadOnDedicatedThread(cts.Token);
        BlockingStubAsset.Entered.Wait(Timeout).ShouldBeTrue();

        cts.Cancel();
        BlockingStubAsset.Release.Set();

        await Should.ThrowAsync<OperationCanceledException>(() => canceledLoad.WaitAsync(Timeout));
        _manager.TryGetAssetByPath(_sourcePath, out _).ShouldBeFalse();
        BlockingStubAsset.Instances.TryPeek(out BlockingStubAsset? canceledAsset).ShouldBeTrue();
        canceledAsset.IsDestroyed.ShouldBeTrue();

        // A different thread proves that the gate is free. Monitor is re-entrant, so the same thread would not.
        BlockingStubAsset? asset = await StartLoadOnDedicatedThread(CancellationToken.None).WaitAsync(Timeout);
        asset.ShouldNotBeNull();
        asset.ShouldNotBeSameAs(canceledAsset);
        asset.Payload.ShouldBe("payload");
        _manager.TryGetAssetByPath(_sourcePath, out XRAsset? cached).ShouldBeTrue();
        cached.ShouldBeSameAs(asset);
    }

    [Test]
    public async Task LoadAsync_CanceledThenReloaded_ReloadedGraphOwnsPersistentIds()
    {
        using var cts = new CancellationTokenSource();
        Task<BlockingStubAsset?> canceledLoad = StartLoadOnDedicatedThread(cts.Token);
        BlockingStubAsset.Entered.Wait(Timeout).ShouldBeTrue();
        cts.Cancel();
        BlockingStubAsset.Release.Set();
        await Should.ThrowAsync<OperationCanceledException>(() => canceledLoad.WaitAsync(Timeout));

        BlockingStubAsset.Instances.TryPeek(out BlockingStubAsset? canceledRoot).ShouldBeTrue();
        ChildStubAsset canceledChild = canceledRoot.Child.ShouldNotBeNull();

        // Force a new import so that the reload makes new objects with the same persistent IDs.
        Directory.Delete(_cachePath, recursive: true);
        Directory.CreateDirectory(_cachePath);

        BlockingStubAsset? reloaded = await StartLoadOnDedicatedThread(CancellationToken.None).WaitAsync(Timeout);
        XRObjectBase.ProcessPendingDestructions();

        reloaded.ShouldNotBeNull();
        reloaded.ShouldNotBeSameAs(canceledRoot);
        ChildStubAsset reloadedChild = reloaded.Child.ShouldNotBeNull();
        reloaded.ID.ShouldBe(BlockingStubAsset.RootId);
        reloadedChild.ID.ShouldBe(BlockingStubAsset.ChildId);

        XRObjectBase.ObjectsCache.TryGetValue(BlockingStubAsset.RootId, out XRObjectBase? cachedRoot).ShouldBeTrue();
        cachedRoot.ShouldBeSameAs(reloaded);
        XRObjectBase.ObjectsCache.TryGetValue(BlockingStubAsset.ChildId, out XRObjectBase? cachedChild).ShouldBeTrue();
        cachedChild.ShouldBeSameAs(reloadedChild);

        canceledRoot.IsDestroyed.ShouldBeTrue();
        canceledChild.IsDestroyed.ShouldBeTrue();
        foreach (XRObjectBase value in XRObjectBase.ObjectsCache.Values)
        {
            ReferenceEquals(value, canceledRoot).ShouldBeFalse();
            ReferenceEquals(value, canceledChild).ShouldBeFalse();
        }
    }

    [Test]
    public async Task LoadAsync_FirstWaiterCanceled_SecondWaiterWithOwnTokenCompletes()
    {
        using var firstCts = new CancellationTokenSource();
        using var secondCts = new CancellationTokenSource();
        using var contended = new ManualResetEventSlim(false);
        _manager.AssetLoadGateContended += _ => contended.Set();

        Task<BlockingStubAsset?> firstLoad = StartLoadOnDedicatedThread(firstCts.Token);
        BlockingStubAsset.Entered.Wait(Timeout).ShouldBeTrue();
        Task<BlockingStubAsset?> secondLoad = StartLoadOnDedicatedThread(secondCts.Token);
        contended.Wait(Timeout).ShouldBeTrue();

        firstCts.Cancel();
        BlockingStubAsset.Release.Set();

        await Should.ThrowAsync<OperationCanceledException>(() => firstLoad.WaitAsync(Timeout));
        BlockingStubAsset? asset = await secondLoad.WaitAsync(Timeout);
        asset.ShouldNotBeNull();
        asset.Payload.ShouldBe("payload");
        _manager.TryGetAssetByPath(_sourcePath, out XRAsset? cached).ShouldBeTrue();
        cached.ShouldBeSameAs(asset);
    }

    [Test]
    public async Task LoadAsync_WaiterCanceledWhileGateHeld_StopsWithoutWaitingForTheOtherLoad()
    {
        using var contended = new ManualResetEventSlim(false);
        _manager.AssetLoadGateContended += _ => contended.Set();

        Task<BlockingStubAsset?> firstLoad = StartLoadOnDedicatedThread(CancellationToken.None);
        BlockingStubAsset.Entered.Wait(Timeout).ShouldBeTrue();

        using var waitingCts = new CancellationTokenSource();
        Task<BlockingStubAsset?> waitingLoad = StartLoadOnDedicatedThread(waitingCts.Token);

        // The waiter is now in the poll loop of the path gate.
        contended.Wait(Timeout).ShouldBeTrue();
        waitingCts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => waitingLoad.WaitAsync(Timeout));
        firstLoad.IsCompleted.ShouldBeFalse();

        BlockingStubAsset.Release.Set();
        BlockingStubAsset? asset = await firstLoad.WaitAsync(Timeout);
        asset.ShouldNotBeNull();
        BlockingStubAsset.LoadCount.ShouldBe(1);
    }

    [Test]
    public async Task LoadAsync_OuterCanceled_InnerAssetsCachedDuringTheLoadSurvive()
    {
        string assetsPath = Path.GetDirectoryName(_sourcePath)!;
        string outerPath = Path.Combine(assetsPath, "outer.neststub");
        NestingStubAsset.Manager = _manager;
        NestingStubAsset.TypedInnerPath = Path.GetFullPath(Path.Combine(assetsPath, "typed.innerstub"));
        NestingStubAsset.GenericInnerPath = Path.GetFullPath(Path.Combine(assetsPath, "generic.innerstub"));
        File.WriteAllText(outerPath, "outer");
        File.WriteAllText(NestingStubAsset.TypedInnerPath, "typed");
        File.WriteAllText(NestingStubAsset.GenericInnerPath, "generic");
        InnerStubAsset.LoadCount = 0;

        using var cts = new CancellationTokenSource();
        Task<NestingStubAsset?> outerLoad = Task.Factory.StartNew(
            () => _manager.LoadAsync<NestingStubAsset>(outerPath, JobPriority.Normal, bypassJobThread: true, cts.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        BlockingStubAsset.Entered.Wait(Timeout).ShouldBeTrue();
        cts.Cancel();
        BlockingStubAsset.Release.Set();
        await Should.ThrowAsync<OperationCanceledException>(() => outerLoad.WaitAsync(Timeout));
        XRObjectBase.ProcessPendingDestructions();

        _manager.TryGetAssetByPath(Path.GetFullPath(outerPath), out _).ShouldBeFalse();
        AssertInnerAssetSurvived(NestingStubAsset.TypedInnerPath, "typed");
        AssertInnerAssetSurvived(NestingStubAsset.GenericInnerPath, "generic");
        InnerStubAsset.LoadCount.ShouldBe(2);
    }

    private void AssertInnerAssetSurvived(string innerPath, string payload)
    {
        _manager.TryGetAssetByPath(innerPath, out XRAsset? cached).ShouldBeTrue();
        InnerStubAsset inner = cached.ShouldBeOfType<InnerStubAsset>();
        inner.IsDestroyed.ShouldBeFalse();
        inner.Payload.ShouldBe(payload);
        XRObjectBase.ObjectsCache.TryGetValue(inner.ID, out XRObjectBase? registered).ShouldBeTrue();
        registered.ShouldBeSameAs(inner);
        _manager.Load(innerPath, typeof(InnerStubAsset), JobPriority.Normal, bypassJobThread: true).ShouldBeSameAs(inner);
    }

    /// <summary>
    /// Starts a load on a new thread. The load bypasses the job thread, so it runs on that thread.
    /// </summary>
    private Task<BlockingStubAsset?> StartLoadOnDedicatedThread(CancellationToken cancellationToken)
        => Task.Factory.StartNew(
            () => _manager.LoadAsync<BlockingStubAsset>(_sourcePath, JobPriority.Normal, bypassJobThread: true, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

    [XR3rdPartyExtensions("cancelstub")]
    private sealed class BlockingStubAsset : XRAsset
    {
        public static readonly ManualResetEventSlim Entered = new(false);
        public static readonly ManualResetEventSlim Release = new(false);
        public static readonly ConcurrentQueue<BlockingStubAsset> Instances = new();
        public static int LoadCount;
        public static Guid RootId;
        public static Guid ChildId;

        public string? Payload { get; set; }
        public ChildStubAsset? Child { get; set; }

        public static void Reset()
        {
            Entered.Reset();
            Release.Reset();
            Instances.Clear();
            LoadCount = 0;
            RootId = Guid.NewGuid();
            ChildId = Guid.NewGuid();
        }

        public override bool Load3rdParty(string filePath)
        {
            Interlocked.Increment(ref LoadCount);
            Instances.Enqueue(this);
            AdoptPersistentID(RootId);
            var child = new ChildStubAsset();
            child.AdoptPersistentID(ChildId);
            Child = child;
            Entered.Set();
            Release.Wait(Timeout);
            Payload = File.ReadAllText(filePath);
            return true;
        }
    }

    private sealed class ChildStubAsset : XRAsset
    {
    }

    /// <summary>
    /// Loads two inner assets, one through the typed route and one through the generic route, then blocks.
    /// </summary>
    [XR3rdPartyExtensions("neststub")]
    private sealed class NestingStubAsset : XRAsset
    {
        public static AssetManager? Manager;
        public static string TypedInnerPath = string.Empty;
        public static string GenericInnerPath = string.Empty;

        public InnerStubAsset? TypedInner { get; set; }
        public InnerStubAsset? GenericInner { get; set; }

        public override bool Load3rdParty(string filePath)
        {
            AssetManager manager = Manager.ShouldNotBeNull();
            TypedInner = manager.Load(TypedInnerPath, typeof(InnerStubAsset), JobPriority.Normal, bypassJobThread: true) as InnerStubAsset;
            GenericInner = manager.Load<InnerStubAsset>(GenericInnerPath, JobPriority.Normal, bypassJobThread: true);
            BlockingStubAsset.Entered.Set();
            BlockingStubAsset.Release.Wait(Timeout);
            return true;
        }
    }

    [XR3rdPartyExtensions("innerstub")]
    private sealed class InnerStubAsset : XRAsset
    {
        public static int LoadCount;

        public string? Payload { get; set; }

        public override bool Load3rdParty(string filePath)
        {
            Interlocked.Increment(ref LoadCount);
            Payload = File.ReadAllText(filePath);
            return true;
        }
    }
}
