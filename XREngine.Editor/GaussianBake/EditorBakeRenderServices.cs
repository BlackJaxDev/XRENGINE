using XREngine.Components;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Rendering;
using XREngine.Rendering.Models.Gaussian.Baking.Views;
using XREngine.Scene;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Renders bake content with the editor engine. The first call for an input builds the bake world, starts it in
/// edit mode on the update thread, and makes it the world of every window, so that the world lights are collected
/// and published each frame. Each capture then runs as a render-thread coroutine with a detached offscreen viewport.
/// </summary>
public sealed class EditorBakeRenderServices : IBakeRenderServices
{
    /// <summary>The longest time that one capture call can take, warm-up included.</summary>
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromMinutes(10);

    private readonly SceneNode _stage;
    private CookedBakeInput? _activeInput;
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
        (IRuntimeRenderWorld renderWorld, RenderInfo3D[] targets) = await InvokeOnUpdateThreadAsync(
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
        _renderWorld = renderWorld;
        _targets = targets;
        _contentBounds = contentBounds;
        Debug.Out($"[GaussianBake] Bake world '{world.Name}' is active with {targets.Length} render targets.");
    }

    private static (IRuntimeRenderWorld RenderWorld, RenderInfo3D[] Targets) ActivateWorld(XRWorld world, CookedBakeInput input)
    {
        RuntimeWorld runtimeWorld = Engine.GetOrCreateWorld(world);
        Task beginEdit = RuntimeWorldHostServices.Current?.BeginEditModeAsync(runtimeWorld) ?? runtimeWorld.BeginPlayAsync();
        beginEdit.GetAwaiter().GetResult();
        IRuntimeRenderWorld renderWorld = Engine.GetOrCreateRenderWorld(world);
        return (renderWorld, CollectTargets(input));
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
