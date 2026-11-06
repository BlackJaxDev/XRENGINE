using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XREngine;
using XREngine.Core.Files;
using XREngine.Data;
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
    private string _sourcePath = string.Empty;
    private AssetManager _manager = null!;
    private IDisposable? _factory;

    [SetUp]
    public void SetUp()
    {
        _rootPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "AssetLoadCancellation", Guid.NewGuid().ToString("N"));
        string assetsPath = Path.Combine(_rootPath, "Assets");
        string cachePath = Path.Combine(_rootPath, "Cache");
        Directory.CreateDirectory(assetsPath);
        Directory.CreateDirectory(cachePath);

        _sourcePath = Path.GetFullPath(Path.Combine(assetsPath, "sample.cancelstub"));
        File.WriteAllText(_sourcePath, "payload");

        _factory = RuntimeCookedBinarySerializer.RegisterRuntimeFactory<BlockingStubAsset>(static () => new BlockingStubAsset());
        _manager = new AssetManager
        {
            MonitorGameAssetsForChanges = false,
            GameAssetsPath = assetsPath,
            GameCachePath = cachePath,
        };
        BlockingStubAsset.Reset();
    }

    [TearDown]
    public void TearDown()
    {
        BlockingStubAsset.Release.Set();
        _manager.Dispose();
        _factory?.Dispose();
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
    public async Task LoadAsync_CanceledDuringLoad_ReleasesGateAndLaterLoadSucceeds()
    {
        using var cts = new CancellationTokenSource();
        Task<BlockingStubAsset?> canceledLoad = StartLoad(cts.Token);
        BlockingStubAsset.Entered.Wait(Timeout).ShouldBeTrue();

        cts.Cancel();
        BlockingStubAsset.Release.Set();

        await Should.ThrowAsync<OperationCanceledException>(() => canceledLoad.WaitAsync(Timeout));
        _manager.TryGetAssetByPath(_sourcePath, out _).ShouldBeFalse();

        BlockingStubAsset? asset = await StartLoad(CancellationToken.None).WaitAsync(Timeout);
        asset.ShouldNotBeNull();
        asset.Payload.ShouldBe("payload");
        _manager.TryGetAssetByPath(_sourcePath, out XRAsset? cached).ShouldBeTrue();
        cached.ShouldBeSameAs(asset);
    }

    [Test]
    public async Task LoadAsync_FirstWaiterCanceled_SecondWaiterWithOwnTokenCompletes()
    {
        using var firstCts = new CancellationTokenSource();
        using var secondCts = new CancellationTokenSource();
        Task<BlockingStubAsset?> firstLoad = StartLoad(firstCts.Token);
        BlockingStubAsset.Entered.Wait(Timeout).ShouldBeTrue();
        Task<BlockingStubAsset?> secondLoad = StartLoad(secondCts.Token);

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
        Task<BlockingStubAsset?> firstLoad = StartLoad(CancellationToken.None);
        BlockingStubAsset.Entered.Wait(Timeout).ShouldBeTrue();

        using var waitingCts = new CancellationTokenSource();
        Task<BlockingStubAsset?> waitingLoad = StartLoad(waitingCts.Token);
        waitingCts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => waitingLoad.WaitAsync(Timeout));
        firstLoad.IsCompleted.ShouldBeFalse();

        BlockingStubAsset.Release.Set();
        BlockingStubAsset? asset = await firstLoad.WaitAsync(Timeout);
        asset.ShouldNotBeNull();
        BlockingStubAsset.LoadCount.ShouldBe(1);
    }

    private Task<BlockingStubAsset?> StartLoad(CancellationToken cancellationToken)
        => Task.Run(() => _manager.LoadAsync<BlockingStubAsset>(_sourcePath, JobPriority.Normal, bypassJobThread: true, cancellationToken));

    [XR3rdPartyExtensions("cancelstub")]
    private sealed class BlockingStubAsset : XRAsset
    {
        public static readonly ManualResetEventSlim Entered = new(false);
        public static readonly ManualResetEventSlim Release = new(false);
        public static int LoadCount;

        public string? Payload { get; set; }

        public static void Reset()
        {
            Entered.Reset();
            Release.Reset();
            LoadCount = 0;
        }

        public override bool Load3rdParty(string filePath)
        {
            Interlocked.Increment(ref LoadCount);
            Entered.Set();
            Release.Wait(Timeout);
            Payload = File.ReadAllText(filePath);
            return true;
        }
    }
}
