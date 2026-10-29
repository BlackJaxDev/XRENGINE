using System.Threading.Channels;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Models.Gaussian.Baking.Build;
using XREngine.Rendering.Models.Gaussian.Cache;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Checks the acceptance of chunks when more than one loader accepts the chunks of one cache, as two components do
/// when the asset manager gives them the same cached root asset.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class GaussianCacheChunkLoaderTests
{
    /// <summary>A small chunk limit, so that the cache has many chunks.</summary>
    private const int SplitChunkBytes = 16_384;

    /// <summary>The path of the root asset. The chunks are in its folder.</summary>
    private static readonly string RootPath = Path.Combine("cache", "root.asset");

    [Test]
    public void AcceptChunk_TwoLoadersReadTheSameNextChunk_DecodeEachChunkOneTime()
    {
        GaussianCacheBuildResult build = GaussianCacheTestSphere.Shared.Build(GaussianCacheLoopMode.Loop, SplitChunkBytes);
        GaussianCache cache = NewCache(build);
        DecodeCounter decodes = new(cache);

        // Both loaders read the same next chunk index and load that chunk. Loader A accepts it first.
        while (!cache.IsComplete)
        {
            int nextOfA = cache.NextChunkIndex;
            int nextOfB = cache.NextChunkIndex;
            cache.AcceptChunk(build.Chunks[nextOfA - 1]);
            cache.AcceptChunk(build.Chunks[nextOfB - 1]);
        }

        decodes.Count.ShouldBe(build.Chunks.Length, "Each chunk after chunk 0 decodes one time.");
        AssertSameDecode(cache, GaussianCacheTestSphere.Load(build));
    }

    [Test]
    public async Task LoadChunksAsync_TwoLoadersInterleaved_DecodeEachChunkOneTime()
    {
        GaussianCacheBuildResult build = GaussianCacheTestSphere.Shared.Build(GaussianCacheLoopMode.Loop, SplitChunkBytes);
        GaussianCache cache = NewCache(build);
        DecodeCounter decodes = new(cache);
        Dictionary<string, GaussianCacheChunk> files = [];
        for (int index = 0; index < build.Chunks.Length; index++)
            files.Add(GaussianCacheChunkLoader.ResolveChunkPath(RootPath, build.Root.ChunkAssetPaths[index]), build.Chunks[index]);
        ScriptedLoads loads = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

        Task loaderA = GaussianCacheChunkLoader.LoadChunksAsync(cache, RootPath, loads.For("A"), static () => true, timeout.Token);
        Task loaderB = GaussianCacheChunkLoader.LoadChunksAsync(cache, RootPath, loads.For("B"), static () => true, timeout.Token);

        // In each step both loaders wait for the same chunk. One loader gets its chunk first and accepts it, and asks for
        // the next chunk. Then the other loader gets the same chunk: its accept counts the chunk as accepted, with no
        // second decode. The first loader changes from step to step.
        ChunkRequest first = await loads.NextAsync(timeout.Token);
        ChunkRequest second = await loads.NextAsync(timeout.Token);
        for (int step = 0; ; step++)
        {
            first.Path.ShouldBe(second.Path);
            first.Loader.ShouldNotBe(second.Loader);
            string winner = step % 2 == 0 ? "B" : "A";
            ChunkRequest early = first.Loader == winner ? first : second;
            ChunkRequest late = first.Loader == winner ? second : first;
            bool last = step + 1 == build.Chunks.Length;

            early.Complete(files[early.Path]);
            ChunkRequest? nextOfWinner = null;
            if (last)
                await (winner == "A" ? loaderA : loaderB).WaitAsync(timeout.Token);
            else
                nextOfWinner = await loads.NextAsync(timeout.Token);
            nextOfWinner?.Loader.ShouldBe(winner);
            decodes.Count.ShouldBe(step + 1);

            late.Complete(files[late.Path]);
            if (last)
                break;

            ChunkRequest nextOfLate = await loads.NextAsync(timeout.Token);
            nextOfLate.Loader.ShouldBe(late.Loader);
            decodes.Count.ShouldBe(step + 1);
            first = nextOfWinner!;
            second = nextOfLate;
        }

        await Task.WhenAll(loaderA, loaderB).WaitAsync(timeout.Token);
        decodes.Count.ShouldBe(build.Chunks.Length, "Each chunk after chunk 0 decodes one time.");
        AssertSameDecode(cache, GaussianCacheTestSphere.Load(build));
    }

    [TestCase("../chunk_0001.asset")]
    [TestCase("C:/chunk_0001.asset")]
    [TestCase("/chunk_0001.asset")]
    public void ResolveChunkPath_PathOutsideCacheFolder_ThrowsInvalidChunkPath(string path)
        => Should.Throw<GaussianCacheFormatException>(() => GaussianCacheChunkLoader.ResolveChunkPath(RootPath, path))
            .DiagnosticCode.ShouldBe(GaussianCacheFormatException.InvalidChunkPathCode);

    [Test]
    public void ResolveChunkPath_RelativePath_IsInRootFolder()
        => GaussianCacheChunkLoader.ResolveChunkPath(RootPath, "chunk_0001.asset").ShouldBe(Path.Combine("cache", "chunk_0001.asset"));

    [Test]
    public async Task LoadChunksAsync_HostileChunkPath_LoadsNothing()
    {
        GaussianCacheBuildResult build = GaussianCacheTestSphere.Shared.Build();
        string[] paths = [.. build.Root.ChunkAssetPaths];
        paths[0] = "../../outside.asset";
        GaussianCache cache = new() { Header = build.Root.Header, RootChunkData = build.Root.RootChunkData, ChunkAssetPaths = paths };
        int loads = 0;

        GaussianCacheFormatException failure = await Should.ThrowAsync<GaussianCacheFormatException>(() => GaussianCacheChunkLoader.LoadChunksAsync(
            cache,
            RootPath,
            (_, _) =>
            {
                loads++;
                return Task.FromResult<GaussianCacheChunk?>(null);
            },
            static () => true,
            CancellationToken.None));

        failure.DiagnosticCode.ShouldBe(GaussianCacheFormatException.InvalidChunkPathCode);
        loads.ShouldBe(0);
    }

    private static GaussianCache NewCache(GaussianCacheBuildResult build)
    {
        GaussianCache cache = new()
        {
            Header = build.Root.Header,
            RootChunkData = build.Root.RootChunkData,
            ChunkAssetPaths = build.Root.ChunkAssetPaths,
        };
        _ = cache.Runtime;
        return cache;
    }

    private static void AssertSameDecode(GaussianCache cache, GaussianCache expected)
    {
        cache.IsComplete.ShouldBeTrue();
        GaussianCacheRuntimeData runtime = cache.Runtime;
        GaussianCacheRuntimeData reference = expected.Runtime;
        for (int level = 0; level < runtime.LevelRecords.Length; level++)
        {
            runtime.GetLoadedNodeCount(level).ShouldBe(cache.Header.SplatLevelCounts[level]);
            runtime.LevelRecords[level].ShouldBe(reference.LevelRecords[level]);
            runtime.ParentIndex[level].ShouldBe(reference.ParentIndex[level]);
        }

        for (int level = 0; level < runtime.DriverLevelIndices.Length; level++)
        {
            runtime.GetLoadedTriangleCount(level).ShouldBe(cache.Header.DriverTriangleCounts[level]);
            runtime.DriverLevelIndices[level].ShouldBe(reference.DriverLevelIndices[level]);
        }

        runtime.Frames.Positions.ShouldBe(reference.Frames.Positions);
        runtime.Frames.DecodedTemporalLevel.ShouldBe(reference.Frames.DecodedTemporalLevel);
    }

    /// <summary>Counts the decodes of a cache: each decode of a chunk after chunk 0 changes the next chunk index one time.</summary>
    private sealed class DecodeCounter
    {
        private int _count;

        public DecodeCounter(GaussianCache cache)
            => cache.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(GaussianCache.NextChunkIndex))
                    Interlocked.Increment(ref _count);
            };

        public int Count => Volatile.Read(ref _count);
    }

    /// <summary>One chunk load that waits until the test completes it.</summary>
    private sealed record ChunkRequest(string Loader, string Path, TaskCompletionSource<GaussianCacheChunk?> Source)
    {
        public void Complete(GaussianCacheChunk chunk)
            => Source.SetResult(chunk);
    }

    /// <summary>Gives each loader a load function whose loads wait in a queue until the test completes them.</summary>
    private sealed class ScriptedLoads
    {
        private readonly Channel<ChunkRequest> _requests = Channel.CreateUnbounded<ChunkRequest>();

        public Func<string, CancellationToken, Task<GaussianCacheChunk?>> For(string loader)
            => (path, _) =>
            {
                TaskCompletionSource<GaussianCacheChunk?> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _requests.Writer.TryWrite(new ChunkRequest(loader, path, source)).ShouldBeTrue();
                return source.Task;
            };

        public ValueTask<ChunkRequest> NextAsync(CancellationToken cancellationToken)
            => _requests.Reader.ReadAsync(cancellationToken);
    }
}
