using System;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Deformation level of detail chooses a minimum cluster level for a far skinned draw on the
/// CPU. The GPU cut then never draws a finer cluster. These tests compare the CPU choice with a
/// port of the skinned cut distance and projected error of the visibility task stage, and check
/// the velocity bookkeeping of a deformed prefix in the output arena.
/// </summary>
[TestFixture]
public sealed class AdvancedDeformationLevelSelectorTests
{
    private const float Threshold = 1.0f;
    private const float Near = 0.05f;
    private const uint ViewHeight = 1600u;
    private static readonly float[] LevelMaxErrors = [0.0004f, 0.0015f, 0.006f, 0.02f, 0.0f];

    [Test]
    public void ChooseLevel_KeepsLevel0NearAndReachesTheCoarsestLevelFar()
    {
        RenderFrameViewSet views = CreateStereoViewSet(Vector3.Zero);
        float pixelScale = CapturePixelScale(views);

        AdvancedDeformationLevelSelector.ChooseLevel(LevelMaxErrors, 0.5f, 1.0f, pixelScale, Threshold).ShouldBe(0u);
        AdvancedDeformationLevelSelector.ChooseLevel(LevelMaxErrors, 10000.0f, 1.0f, pixelScale, Threshold)
            .ShouldBe((uint)LevelMaxErrors.Length - 1u);

        uint previous = 0u;
        for (float distance = 0.5f; distance < 200.0f; distance *= 1.25f)
        {
            uint level = AdvancedDeformationLevelSelector.ChooseLevel(LevelMaxErrors, distance, 1.0f, pixelScale, Threshold);
            level.ShouldBeGreaterThanOrEqualTo(previous, $"distance {distance}");
            previous = level;
        }
    }

    [Test]
    public void ChooseLevel_ReturnsLevel0ForAnUnusableDistance()
    {
        AdvancedDeformationLevelSelector.ChooseLevel(LevelMaxErrors, -1.0f, 1.0f, 800.0f, Threshold).ShouldBe(0u);
        AdvancedDeformationLevelSelector.ChooseLevel(LevelMaxErrors, float.NaN, 1.0f, 800.0f, Threshold).ShouldBe(0u);
        AdvancedDeformationLevelSelector.ChooseLevel([0.001f], 1000.0f, 1.0f, 800.0f, Threshold).ShouldBe(0u);
    }

    [Test]
    public void TryCaptureViews_RejectsAnOrthographicView()
    {
        Matrix4x4 orthographic = Matrix4x4.CreateOrthographic(4.0f, 4.0f, Near, 100.0f);
        RenderFrameViewSet views = RenderFrameViewSet.Create(
            EVrViewRenderMode.SequentialViews,
            EVrVisibilityPolicy.SharedFrameViewSet,
            visibilityGroupCount: 1,
            [CreateView(0u, EVrOutputViewKind.DesktopEditor, Vector3.Zero, orthographic)]);

        AdvancedDeformationLevelSelector.TryCaptureViews(in views, new Vector3[8], out _, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void CpuDistanceIsNeverLargerThanTheGpuSharedDistance()
    {
        Random random = new(1234);
        Vector3[] eyes = new Vector3[8];
        for (int testCase = 0; testCase < 500; testCase++)
        {
            Scenario scenario = Scenario.Create(random);
            RenderFrameViewSet views = CreateStereoViewSet(scenario.Head);
            AdvancedDeformationLevelSelector.TryCaptureViews(in views, eyes, out int eyeCount, out _, out float near)
                .ShouldBeTrue();

            float cpu = AdvancedDeformationLevelSelector.DrawDistance(
                eyes.AsSpan(0, eyeCount), scenario.Sphere, scenario.HasPhysics,
                scenario.ProxyMin, scenario.ProxyMax, scenario.Margin, scenario.Scale, near);
            float gpu = GpuSharedDistance(views, scenario);

            cpu.ShouldBeGreaterThan(0.0f);
            cpu.ShouldBeLessThanOrEqualTo(gpu * 1.0001f, $"case {testCase}");
        }
    }

    [Test]
    public void ChosenLevelNeverClampsAGroupThatTheUnclampedCutDraws()
    {
        Random random = new(5678);
        Vector3[] eyes = new Vector3[8];
        int clampedCases = 0;
        for (int testCase = 0; testCase < 500; testCase++)
        {
            Scenario scenario = Scenario.Create(random);
            RenderFrameViewSet views = CreateStereoViewSet(scenario.Head);
            AdvancedDeformationLevelSelector.TryCaptureViews(
                in views, eyes, out int eyeCount, out float pixelScale, out float near).ShouldBeTrue();
            float cpu = AdvancedDeformationLevelSelector.DrawDistance(
                eyes.AsSpan(0, eyeCount), scenario.Sphere, scenario.HasPhysics,
                scenario.ProxyMin, scenario.ProxyMax, scenario.Margin, scenario.Scale, near);
            uint level = AdvancedDeformationLevelSelector.ChooseLevel(
                LevelMaxErrors, cpu, scenario.Scale, pixelScale, Threshold);
            if (level != 0u)
                clampedCases++;

            // The unclamped cut draws a non-root cluster of level d only when the projected error
            // of its group exceeds the threshold. Every group finer than the chosen level must
            // therefore project to at most the threshold on the GPU.
            float gpuDistance = GpuSharedDistance(views, scenario);
            float gpuPixelScale = GpuPixelScale(views);
            for (int finer = 0; finer < (int)level; finer++)
            {
                float projected = LevelMaxErrors[finer] * scenario.Scale * gpuPixelScale / gpuDistance;
                projected.ShouldBeLessThanOrEqualTo(Threshold, $"case {testCase}, level {finer}");
            }
        }
        clampedCases.ShouldBeGreaterThan(50);
    }

    [Test]
    public void ArenaReportsAGrownPrefixAsMissingVelocityHistory()
    {
        AdvancedDeformedVertexArena arena = new(
            new AdvancedDeformedVertexArenaOptions(
                InitialVertexCapacity: 32u,
                FrameSlotCount: 3,
                OwnerCapacity: 8,
                RetiredGenerationCapacity: 2));
        AdvancedGpuHandle owner = new(1u, 1u);

        bool Frame(ulong frame, uint deformed)
        {
            arena.TryBeginFrame(frame, frame).ShouldBeTrue();
            arena.TryAcquireSlice(owner, 16u, 1u, 1u, newlyVisible: frame == 0UL, out _).ShouldBeTrue();
            bool covered = arena.RecordDeformedVertexCount(owner, deformed);
            arena.ConfirmOwnerHistoryProduced(owner);
            arena.EndFrame(frame + 1UL);
            return covered;
        }

        Frame(0UL, 16u).ShouldBeFalse();
        Frame(1UL, 16u).ShouldBeTrue();
        // A shrinking prefix keeps the history of the vertices that it still deforms.
        Frame(2UL, 6u).ShouldBeTrue();
        Frame(3UL, 6u).ShouldBeTrue();
        // A growing prefix has no history for the new vertices.
        Frame(4UL, 10u).ShouldBeFalse();
        Frame(5UL, 10u).ShouldBeTrue();
    }

    [Test]
    public void ArenaKeepsTheLargestPrefixOfSeveralDrawsOfOneOwner()
    {
        AdvancedDeformedVertexArena arena = new(
            new AdvancedDeformedVertexArenaOptions(
                InitialVertexCapacity: 32u,
                FrameSlotCount: 3,
                OwnerCapacity: 8,
                RetiredGenerationCapacity: 2));
        AdvancedGpuHandle owner = new(2u, 1u);

        arena.TryBeginFrame(0UL, 0UL).ShouldBeTrue();
        arena.TryAcquireSlice(owner, 16u, 1u, 1u, newlyVisible: true, out _).ShouldBeTrue();
        arena.RecordDeformedVertexCount(owner, 6u);
        arena.RecordDeformedVertexCount(owner, 16u);
        arena.EndFrame(1UL);

        arena.TryBeginFrame(1UL, 1UL).ShouldBeTrue();
        arena.TryAcquireSlice(owner, 16u, 1u, 1u, newlyVisible: false, out _).ShouldBeTrue();
        arena.RecordDeformedVertexCount(owner, 16u).ShouldBeTrue();
        arena.RecordDeformedVertexCount(owner, 6u).ShouldBeTrue();
        arena.EndFrame(2UL);
    }

    /// <summary>
    /// The shared distance of the skinned cut for the left eye view and its partner, as
    /// <c>XR_ADV_ClusterCutUseDrawBound</c> computes it: the nearer eye to the nearer of the
    /// instance sphere and the candidate sphere, less the margin times the scale, not below the
    /// near distance.
    /// </summary>
    private static float GpuSharedDistance(RenderFrameViewSet views, in Scenario scenario)
    {
        Vector3 eye0 = views.GetView(0).CameraPositionAndNear.AsVector3();
        Vector3 eye1 = views.GetView(1).CameraPositionAndNear.AsVector3();
        float near = MathF.Max(views.GetView(0).CameraPositionAndNear.W, 1.0e-4f);
        float nearest = SphereDistance(scenario.Sphere, eye0, eye1);
        if (scenario.HasPhysics)
            nearest = MathF.Min(nearest, SphereDistance(scenario.CandidateSphere, eye0, eye1));
        return MathF.Max(nearest - scenario.Margin * scenario.Scale, near);
    }

    private static float SphereDistance(Vector4 sphere, Vector3 eye0, Vector3 eye1)
    {
        Vector3 center = sphere.AsVector3();
        return MathF.Max(MathF.Min(Vector3.Distance(center, eye0), Vector3.Distance(center, eye1)) - sphere.W, 0.0f);
    }

    private static float GpuPixelScale(RenderFrameViewSet views)
    {
        float scale = 0.0f;
        for (int index = 0; index < views.ViewCount; index++)
        {
            Matrix4x4 projection = views.GetView(index).ProjectionMatrix;
            scale = MathF.Max(scale, MathF.Max(MathF.Abs(projection.M11), MathF.Abs(projection.M22)) * ViewHeight * 0.5f);
        }
        return scale;
    }

    private static float CapturePixelScale(RenderFrameViewSet views)
    {
        AdvancedDeformationLevelSelector.TryCaptureViews(in views, new Vector3[8], out _, out float pixelScale, out _)
            .ShouldBeTrue();
        return pixelScale;
    }

    private static RenderFrameViewSet CreateStereoViewSet(Vector3 head)
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * 0.5f, 0.9f, Near, 500.0f);
        Vector3 offset = new(0.032f, 0.0f, 0.0f);
        return RenderFrameViewSet.Create(
            EVrViewRenderMode.SinglePassStereo,
            EVrVisibilityPolicy.SharedFrameViewSet,
            visibilityGroupCount: 1,
            [
                CreateView(0u, EVrOutputViewKind.LeftEye, head - offset, projection),
                CreateView(1u, EVrOutputViewKind.RightEye, head + offset, projection),
            ]);
    }

    private static RenderFrameViewDescriptor CreateView(uint viewId, EVrOutputViewKind kind, Vector3 eye, Matrix4x4 projection)
        => new(
            viewId,
            kind,
            RenderFrameViewDescriptor.InvalidViewId,
            VisibilityGroupIndex: 0,
            OpenXrViewIndex: (int)viewId,
            OutputLayer: viewId,
            RenderFrameViewRect.FromSize(1440u, ViewHeight),
            Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY),
            projection,
            Matrix4x4.Identity,
            default,
            HistoryKey: 100UL + viewId,
            CameraPositionAndNear: new Vector4(eye, Near));

    /// <summary>One random draw: posed sphere, optional physics boxes, margin and scale.</summary>
    private readonly record struct Scenario(
        Vector3 Head,
        Vector4 Sphere,
        bool HasPhysics,
        Vector3 ProxyMin,
        Vector3 ProxyMax,
        Vector4 CandidateSphere,
        float Margin,
        float Scale)
    {
        public static Scenario Create(Random random)
        {
            Vector3 head = new(Next(random, -2.0f, 2.0f), 1.6f, Next(random, -2.0f, 2.0f));
            float distance = Next(random, 0.5f, 80.0f);
            float angle = Next(random, 0.0f, MathF.Tau);
            Vector3 center = head + new Vector3(MathF.Cos(angle) * distance, Next(random, -1.0f, 0.5f), MathF.Sin(angle) * distance);
            float radius = Next(random, 0.3f, 1.5f);
            bool hasPhysics = random.Next(2) == 0;

            // The exact physics box lies inside the proxy box. The GPU patch rewrites the
            // candidate sphere to the center and half diagonal of the exact box.
            Vector3 proxyMin = center - new Vector3(Next(random, 0.3f, 1.5f), Next(random, 0.3f, 1.5f), Next(random, 0.3f, 1.5f));
            Vector3 proxyMax = center + new Vector3(Next(random, 0.3f, 1.5f), Next(random, 0.3f, 1.5f), Next(random, 0.3f, 1.5f));
            Vector3 exactMin = Vector3.Lerp(proxyMin, proxyMax, Next(random, 0.0f, 0.5f));
            Vector3 exactMax = Vector3.Lerp(exactMin, proxyMax, Next(random, 0.1f, 1.0f));
            Vector4 candidate = new((exactMin + exactMax) * 0.5f, Vector3.Distance(exactMin, exactMax) * 0.5f);

            return new Scenario(
                head,
                new Vector4(center, radius),
                hasPhysics,
                proxyMin,
                proxyMax,
                candidate,
                Next(random, 0.0f, 0.05f),
                Next(random, 0.5f, 2.0f));
        }

        private static float Next(Random random, float minimum, float maximum)
            => minimum + (float)random.NextDouble() * (maximum - minimum);
    }
}
