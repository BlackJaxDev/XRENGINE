using System.Numerics;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
using XREngine.Rendering.Models.Gaussian.Baking.Cli;
using XREngine.Rendering.Models.Gaussian.Baking.Color;
using XREngine.Rendering.Models.Gaussian.Baking.Driver;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Motion;
using XREngine.Rendering.Models.Gaussian.Baking.Report;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Rendering.Models.Gaussian.Baking.Views;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;
using XREngine.Scene;

namespace XREngine.Rendering.Models.Gaussian.Baking.Pipeline;

// The steps that read or pose the content: the motion, the frame passes, and the color capture.
public sealed partial class GaussianBakePipeline
{
    /// <summary>
    /// Chooses the motion, samples every frame for the largest vertex distances and the bounds, chooses the grid, breaks
    /// the seam welds that open, and marks the animated meshes. The frame passes run on the content thread.
    /// </summary>
    private async Task<ContentAnalysis> AnalyzeAsync(BakeRun run, GaussianBakeBudget budget, CancellationToken cancellationToken)
    {
        CookedBakeInput input = run.Input!;
        SceneNode[] roots = run.Roots;
        FirstPass pass = await _render.RunOnContentThreadAsync(
            input,
            () => RunFirstPass(input, roots, run.Options, budget),
            cancellationToken).ConfigureAwait(false);
        run.Choice = pass.Choice;
        run.Report.Warnings.AddRange(pass.Warnings);
        foreach (BakeSkippedComponent skipped in pass.Skipped)
            run.Report.Skipped.Add(new BakeReportSkipped { Node = skipped.NodePath, Component = skipped.ComponentType, Reason = skipped.Reason });

        DeformedSurface rest = pass.Rest;
        if (rest.TriangleCount == 0 || !pass.Bounds.IsValid)
            throw new GaussianBakeException(GaussianBakeFailureCodes.ContentEmpty, $"The asset '{run.Options.AssetPath}' has no triangle surface to bake.");

        byte gridBits = GridQuantizer.ChooseGridBits(pass.Bounds, budget.MaxPositionErrorMeters);
        float gridStep = GridStep(new GridQuantizer(pass.Bounds, gridBits));
        bool[] animatedMeshes = MotionClassifier.FindAnimatedMeshes(rest, pass.MaxDisplacement, gridStep);

        // Weld seam-split vertices at rest, and break each weld that opens by more than one grid step in a frame.
        uint[] weld = SeamWelder.CreateRemap(rest, _meshopt);
        if (pass.Choice.FrameCount > 1 && Array.IndexOf(animatedMeshes, true) >= 0)
        {
            BakeMotionChoice choice = pass.Choice;
            await _render.RunOnContentThreadAsync(
                input,
                () =>
                {
                    BakeFrameSampler.ForEachFrame(roots, choice, (_, surface) => SeamWelder.BreakDivergent(weld, surface, gridStep));
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }

        return new ContentAnalysis(pass.Choice, rest, pass.Bounds, gridBits, gridStep, animatedMeshes, weld, pass.CameraPath);
    }

    /// <summary>
    /// Runs on the content thread: chooses the motion, finds the skipped components and the camera path, evaluates the
    /// rest surface (time 0), and samples every frame.
    /// </summary>
    private static FirstPass RunFirstPass(CookedBakeInput input, SceneNode[] roots, BakeSplatPreviewOptions options, GaussianBakeBudget budget)
    {
        List<string> warnings = [];
        BakeMotionChoice choice = input.World is XRWorld world
            ? BakeMotionSelector.ChooseForWorld(world, budget)
            : BakeMotionSelector.ChooseForPrefab(roots[0], options.ClipName, budget, warnings);
        try
        {
            IReadOnlyList<BakeSkippedComponent> skipped = DeformedSurfaceEvaluator.FindSkipped(roots);
            GaussianCacheCameraKey[] path = FindCameraPath(roots);
            DeformedSurface rest = BakeFrameSampler.EvaluateAt(roots, choice, 0.0f);

            float[] restPositions = rest.Positions;
            float[] maxDisplacement = new float[rest.VertexCount];
            AABB restBounds = BoundsOf(restPositions);
            Vector3 min = restBounds.Min;
            Vector3 max = restBounds.Max;
            BakeFrameSampler.ForEachFrame(roots, choice, (_, surface) =>
            {
                float[] positions = surface.Positions;
                for (int vertex = 0; vertex < maxDisplacement.Length; vertex++)
                {
                    int offset = vertex * 3;
                    Vector3 point = new(positions[offset], positions[offset + 1], positions[offset + 2]);
                    Vector3 start = new(restPositions[offset], restPositions[offset + 1], restPositions[offset + 2]);
                    float distance = Vector3.Distance(point, start);
                    if (distance > maxDisplacement[vertex])
                        maxDisplacement[vertex] = distance;
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                }
            });

            return new FirstPass(choice, warnings, skipped, path, rest, maxDisplacement, new AABB(min, max));
        }
        catch
        {
            BakeMotionSelector.ReleaseTemporaryPlayers(choice);
            throw;
        }
    }

    /// <summary>
    /// Runs on the content thread: samples the positions of the given surface vertices in every frame, and the largest
    /// distance of these vertices between time 0 and the end of the motion.
    /// </summary>
    private static DriverTrajectories SampleTrajectories(SceneNode[] roots, BakeMotionChoice choice, DeformedSurface rest, int[] driverSources)
    {
        int[] slotOfVertex = new int[rest.VertexCount];
        Array.Fill(slotOfVertex, -1);
        List<int> vertices = [];
        foreach (int vertex in driverSources)
        {
            if (slotOfVertex[vertex] < 0)
            {
                slotOfVertex[vertex] = vertices.Count;
                vertices.Add(vertex);
            }
        }

        int[] sampled = [.. vertices];
        int frameCount = Math.Max(1, choice.FrameCount);
        long valueCount = (long)frameCount * sampled.Length * 3;
        if (valueCount > Array.MaxLength)
            throw new GaussianBakeException(GaussianBakeFailureCodes.BudgetExceeded, $"The driver motion of {sampled.Length} vertices and {frameCount} frames is too large.");
        float[] positions = new float[valueCount];
        BakeFrameSampler.ForEachFrame(roots, choice, (frame, surface) =>
        {
            float[] source = surface.Positions;
            int destination = frame * sampled.Length * 3;
            for (int slot = 0; slot < sampled.Length; slot++)
            {
                int offset = sampled[slot] * 3;
                positions[destination++] = source[offset];
                positions[destination++] = source[offset + 1];
                positions[destination++] = source[offset + 2];
            }
        });

        DeformedSurface end = BakeFrameSampler.EvaluateLoopEnd(roots, choice);
        float maxEndDistance = 0.0f;
        foreach (int vertex in sampled)
        {
            int offset = vertex * 3;
            Vector3 start = new(rest.Positions[offset], rest.Positions[offset + 1], rest.Positions[offset + 2]);
            Vector3 last = new(end.Positions[offset], end.Positions[offset + 1], end.Positions[offset + 2]);
            float distance = Vector3.Distance(start, last);
            if (!(distance <= maxEndDistance))
                maxEndDistance = distance;
        }

        return new DriverTrajectories(slotOfVertex, sampled.Length, frameCount, positions, maxEndDistance);
    }

    /// <summary>
    /// Bakes the leaf colors from renders of the content posed at time 0: 32 views around the bounds, and for a world 32
    /// more views from the center of the bounds. The pose stays for all capture batches and is put back after them.
    /// </summary>
    private async Task<float> BakeColorsAsync(BakeRun run, ContentAnalysis analysis, BakedSplat[] leaves, CancellationToken cancellationToken)
    {
        CookedBakeInput input = run.Input!;
        List<BakeCameraPose> views = [.. BakeViewPlanner.PlanOrbitViews(analysis.Bounds, OrbitViewCount)];
        if (run.Options.Kind == GaussianBakeKind.World)
            views.AddRange(BakeViewPlanner.PlanInteriorViews(analysis.Bounds, InteriorViewCount));

        LitProjectionColorBaker baker = new();
        SplatColorBakeContext context = new()
        {
            Leaves = leaves,
            Rest = analysis.Rest,
            Views = views,
            Width = ViewWidth,
            Height = ViewHeight,
            Capture = (poses, token) => _render.CaptureViewsAsync(input, poses, ViewWidth, ViewHeight, token),
        };

        SceneNode[] roots = run.Roots;
        BakeMotionChoice choice = analysis.Choice;
        BakePoseHold hold = await _render.RunOnContentThreadAsync(
            input,
            () => BakePoseHold.Begin(roots, choice, 0.0f),
            cancellationToken).ConfigureAwait(false);
        float colorScale;
        try
        {
            colorScale = await baker.BakeAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await RunReleaseAsync(input, hold.Dispose).ConfigureAwait(false);
        }

        if (baker.LastSeenLeafCount == 0)
            run.Report.Warnings.Add(NoLeafSeenWarning);
        return colorScale;
    }

    /// <summary>Returns true when an active renderable component under the roots has a mesh with vertices.</summary>
    private static bool HasRenderableMesh(SceneNode[] roots)
    {
        bool found = false;
        foreach (SceneNode root in roots)
        {
            root.IterateComponents<RenderableComponent>(component =>
            {
                if (found || !component.IsActive)
                    return;

                foreach (RenderableMesh mesh in component.Meshes)
                {
                    if (mesh.LODs.First?.Value.Renderer?.Mesh is { VertexCount: > 0 })
                    {
                        found = true;
                        return;
                    }
                }
            }, true);
        }

        return found;
    }

    /// <summary>
    /// Returns the keys of the first active <see cref="PreviewCameraPathComponent"/> that has keys, in depth-first order
    /// over the roots, or an empty array.
    /// </summary>
    private static GaussianCacheCameraKey[] FindCameraPath(SceneNode[] roots)
    {
        GaussianCacheCameraKey[]? keys = null;
        foreach (SceneNode root in roots)
        {
            root.IterateComponents<PreviewCameraPathComponent>(component =>
            {
                if (keys is null && component.IsActive && component.SceneNode.IsActiveSelf && component.Keys.Length > 0)
                    keys = [.. component.Keys];
            }, true);
        }

        return keys ?? [];
    }

    /// <summary>Marks each leaf on a mesh that is not animated as static.</summary>
    private static void MarkStaticSplats(BakedSplat[] leaves, DeformedSurface rest, bool[] animatedMeshes)
    {
        for (int i = 0; i < leaves.Length; i++)
            leaves[i].IsStatic = !animatedMeshes[rest.TriangleMesh[leaves[i].SourceTriangle]];
    }

    /// <summary>Returns the largest grid step on an axis: two times the largest quantization error.</summary>
    private static float GridStep(GridQuantizer quantizer)
        => 2.0f * quantizer.MaxErrorMeters;

    /// <summary>The result of the first frame pass.</summary>
    private sealed record FirstPass(
        BakeMotionChoice Choice,
        List<string> Warnings,
        IReadOnlyList<BakeSkippedComponent> Skipped,
        GaussianCacheCameraKey[] CameraPath,
        DeformedSurface Rest,
        float[] MaxDisplacement,
        AABB Bounds);

    /// <summary>The motion, the rest surface, the grid, and the classification of the content.</summary>
    private sealed record ContentAnalysis(
        BakeMotionChoice Choice,
        DeformedSurface Rest,
        AABB Bounds,
        byte GridBits,
        float GridStep,
        bool[] AnimatedMeshes,
        uint[] Weld,
        GaussianCacheCameraKey[] CameraPath);

    /// <summary>
    /// The positions of some surface vertices in every frame, as x, y, and z, frame by frame, plus the largest distance
    /// of these vertices between time 0 and the end of the motion.
    /// </summary>
    private sealed record DriverTrajectories(int[] SlotOfVertex, int SlotCount, int FrameCount, float[] Positions, float MaxEndDistance)
    {
        /// <summary>Returns true when every surface vertex in <paramref name="sources"/> has sampled positions.</summary>
        public bool Covers(int[] sources)
        {
            foreach (int vertex in sources)
            {
                if ((uint)vertex >= (uint)SlotOfVertex.Length || SlotOfVertex[vertex] < 0)
                    return false;
            }

            return true;
        }

        /// <summary>Returns the position of a surface vertex in a frame.</summary>
        public Vector3 PositionOf(int vertex, int frame)
        {
            int offset = ((frame * SlotCount) + SlotOfVertex[vertex]) * 3;
            return new Vector3(Positions[offset], Positions[offset + 1], Positions[offset + 2]);
        }
    }
}
