using XREngine.Components;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Build;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Rendering;
using XREngine.Rendering.Models.Gaussian.Baking.Video;
using XREngine.Rendering.Models.Gaussian.Baking.Views;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Scene;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Renders bake content with the editor engine. The first call for an input builds the bake world, starts it in
/// edit mode on the update thread, and makes it the world of every window, so that the world lights are collected
/// and published each frame. Each capture then runs as a render-thread coroutine with a detached offscreen viewport.
/// </summary>
public sealed class EditorBakeRenderServices : IBakeRenderServices
{
    /// <summary>The name of the scene node that plays the cache during a loop recording.</summary>
    public const string LoopNodeName = "GaussianBakeLoopCache";

    /// <summary>The longest time that one capture call can take, warm-up included.</summary>
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The longest time that a loop recording can take, plus one second for each frame.</summary>
    private static readonly TimeSpan LoopBaseTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The longest time to wait for the update thread to remove the loop node.</summary>
    private static readonly TimeSpan NodeRemovalWait = TimeSpan.FromSeconds(30);

    private readonly SceneNode _stage;
    private CookedBakeInput? _activeInput;
    private RuntimeWorld? _runtimeWorld;
    private IRuntimeRenderWorld? _renderWorld;
    private RenderInfo3D[] _targets = [];
    private AABB _contentBounds;

    /// <summary>Creates the render services.</summary>
    /// <param name="stage">The root node of the preview stage. The bake world takes ownership of the node.</param>
    public EditorBakeRenderServices(SceneNode stage)
        => _stage = stage ?? throw new ArgumentNullException(nameof(stage));

    /// <inheritdoc />
    public async Task<float[][]> CaptureViewsAsync(
        CookedBakeInput input,
        IReadOnlyList<BakeCameraPose> poses,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(poses);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (poses.Count == 0)
            return [];

        await EnsureWorldAsync(input, cancellationToken).ConfigureAwait(false);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CaptureTimeout);
        BakeViewCapture capture = new(_renderWorld!, _targets, _contentBounds, poses, width, height, timeout.Token);
        RuntimeEngine.AddRenderThreadCoroutine(capture.Step, "GaussianBake.CaptureViews", RenderThreadJobKind.RequiresGraphicsContext);
        try
        {
            // The wait also ends when the render thread never runs the coroutine.
            return await capture.Completion.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GaussianBakeException(
                GaussianBakeFailureCodes.CaptureFailed,
                $"The capture of {poses.Count} views did not complete in {CaptureTimeout.TotalMinutes:F0} minutes.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The cache plays on a new root node (<see cref="LoopNodeName"/>) of the bake world, and the capture draws only
    /// that node. The node is removed and destroyed after the recording. The time limit is 10 minutes plus one second
    /// for each frame.
    /// </remarks>
    public async Task RecordLoopAsync(
        CookedBakeInput input,
        GaussianCacheBuildResult cache,
        IReadOnlyList<BakeCameraPose> poses,
        float fps,
        FfmpegEncoder encoder,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(poses);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (poses.Count == 0)
            throw new ArgumentException("A loop needs at least one pose.", nameof(poses));
        if (!float.IsFinite(fps) || fps <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(fps), fps, "The frame rate must be more than 0.");

        string directory = CreateOutputDirectory(outputDirectory);
        await EnsureWorldAsync(input, cancellationToken).ConfigureAwait(false);

        // The component gets a cache with every chunk accepted, so the first upload holds the full cache.
        GaussianCache playable = CreatePlayableCache(cache);
        SceneNode node = new(LoopNodeName);
        GaussianCacheComponent component = node.AddComponent<GaussianCacheComponent>()
            ?? throw new InvalidOperationException("The loop node cannot add a gaussian cache component.");
        component.Playing = false;
        component.Cache = playable;
        AABB bounds = SplatBounds(component, playable);

        RuntimeWorld runtimeWorld = _runtimeWorld!;
        LoopRecorder recorder = new(component, poses, fps);
        TimeSpan limit = LoopBaseTimeout + TimeSpan.FromSeconds(poses.Count);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            await InvokeOnUpdateThreadAsync(
                () =>
                {
                    runtimeWorld.RootNodes.Add(node);
                    return true;
                },
                timeout.Token).ConfigureAwait(false);
            await recorder.RecordAsync(_renderWorld!, bounds, encoder, directory, timeout.Token).ConfigureAwait(false);
            Debug.Out($"[GaussianBake] Recorded a loop of {poses.Count} frames at {fps} fps into '{directory}'.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GaussianBakeException(
                GaussianBakeFailureCodes.EncodeFailed,
                $"The loop recording did not complete in {limit.TotalMinutes:F0} minutes ({recorder.FramesWritten} of {poses.Count} frames written).");
        }
        finally
        {
            await RemoveLoopNodeAsync(runtimeWorld, node).ConfigureAwait(false);
        }
    }

    private async Task EnsureWorldAsync(CookedBakeInput input, CancellationToken cancellationToken)
    {
        if (ReferenceEquals(_activeInput, input))
            return;
        if (_activeInput is not null)
            throw new InvalidOperationException("The bake render services render only one input.");

        if (!BakeWorldFactory.TryComputeContentBounds(input, out AABB contentBounds))
            throw new GaussianBakeException(GaussianBakeFailureCodes.ContentEmpty, $"The asset '{input.AssetPath}' has no renderable mesh with valid bounds.");

        XRWorld world = BakeWorldFactory.Create(input, _stage);

        // World activation follows the play-mode transitions of the editor: it runs on the update thread.
        (RuntimeWorld runtimeWorld, IRuntimeRenderWorld renderWorld, RenderInfo3D[] targets) = await InvokeOnUpdateThreadAsync(
            () => ActivateWorld(world, input),
            cancellationToken).ConfigureAwait(false);

        await InvokeOnRenderThreadAsync(
            () =>
            {
                for (int i = 0; i < RuntimeEngine.Windows.Count; i++)
                    RuntimeEngine.Windows[i].SetWorld(renderWorld);
            },
            cancellationToken).ConfigureAwait(false);

        _activeInput = input;
        _runtimeWorld = runtimeWorld;
        _renderWorld = renderWorld;
        _targets = targets;
        _contentBounds = contentBounds;
        Debug.Out($"[GaussianBake] Bake world '{world.Name}' is active with {targets.Length} render targets.");
    }

    private static (RuntimeWorld RuntimeWorld, IRuntimeRenderWorld RenderWorld, RenderInfo3D[] Targets) ActivateWorld(XRWorld world, CookedBakeInput input)
    {
        RuntimeWorld runtimeWorld = Engine.GetOrCreateWorld(world);
        Task beginEdit = RuntimeWorldHostServices.Current?.BeginEditModeAsync(runtimeWorld) ?? runtimeWorld.BeginPlayAsync();
        beginEdit.GetAwaiter().GetResult();
        IRuntimeRenderWorld renderWorld = Engine.GetOrCreateRenderWorld(world);
        return (runtimeWorld, renderWorld, CollectTargets(input));
    }

    /// <summary>
    /// Collects the render infos of the content. Light components are not content: their render infos are editor previews.
    /// </summary>
    private static RenderInfo3D[] CollectTargets(CookedBakeInput input)
    {
        List<RenderInfo3D> targets = [];
        foreach (SceneNode root in BakeWorldFactory.EnumerateContentRoots(input))
        {
            root.IterateComponents(component =>
            {
                if (component is not IRenderable renderable || component is LightComponent)
                    return;

                foreach (RenderInfo info in renderable.RenderedObjects)
                    if (info is RenderInfo3D info3D)
                        targets.Add(info3D);
            }, iterateChildHierarchy: true);
        }

        return [.. targets];
    }

    /// <summary>
    /// Makes a new cache from a build result and accepts every chunk, so that the loop plays the cache with every level
    /// loaded. The build result does not change.
    /// </summary>
    private static GaussianCache CreatePlayableCache(GaussianCacheBuildResult build)
    {
        GaussianCache cache = new()
        {
            Name = build.Root.Name,
            Header = build.Root.Header,
            RootChunkData = build.Root.RootChunkData,
            ChunkAssetPaths = build.Root.ChunkAssetPaths,
        };
        foreach (GaussianCacheChunk chunk in build.Chunks)
            cache.AcceptChunk(chunk);
        if (!cache.IsComplete)
            throw new InvalidOperationException($"The cache accepted {cache.NextChunkIndex} of {cache.Header.Chunks.Length} chunks.");
        return cache;
    }

    /// <summary>Returns the world bounds of the splats: the culling volume of the component, or else the cache bounds.</summary>
    private static AABB SplatBounds(GaussianCacheComponent component, GaussianCache cache)
    {
        foreach (RenderInfo info in component.RenderedObjects)
            if (info is RenderInfo3D { LocalCullingVolume: { } volume } && volume.IsValid)
                return volume;
        return cache.Header.Bounds;
    }

    private static string CreateOutputDirectory(string outputDirectory)
    {
        try
        {
            string directory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new GaussianBakeException(GaussianBakeFailureCodes.EncodeFailed, $"The loop folder '{outputDirectory}' cannot be created: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the loop node from the world and destroys it, which releases the GPU data of its cache component.
    /// A failure here only logs a warning, so that it does not hide the result of the recording.
    /// </summary>
    private static async Task RemoveLoopNodeAsync(RuntimeWorld world, SceneNode node)
    {
        try
        {
            await InvokeOnUpdateThreadAsync(
                () =>
                {
                    world.RootNodes.Remove(node);
                    node.Destroy();
                    return true;
                },
                CancellationToken.None).WaitAsync(NodeRemovalWait).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GaussianBake] The loop node '{node.Name}' was not removed from the bake world: {ex.Message}");
        }
    }

    private static Task<T> InvokeOnUpdateThreadAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.EnqueueUpdateThreadTask(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task.WaitAsync(cancellationToken);
    }

    private static Task InvokeOnRenderThreadAsync(Action work, CancellationToken cancellationToken)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RuntimeEngine.EnqueueRenderThreadTask(
            () =>
            {
                try
                {
                    work();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            },
            "GaussianBake.SetWindowWorld",
            RenderThreadJobKind.RequiresGraphicsContext);
        return completion.Task.WaitAsync(cancellationToken);
    }
}
