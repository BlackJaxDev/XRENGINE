using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Occlusion;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class MaskedSoftwareOcclusionCullingTests
{
    [Test]
    public void CoveredBufferRectRejectsBehindButAllowsCloser()
    {
        MaskedOcclusionBuffer buffer = new();
        buffer.Resize(8, 4);
        buffer.Clear();

        for (int y = 0; y < buffer.Height; y++)
        {
            for (int x = 0; x < buffer.Width; x++)
                buffer.TryWritePixel(x, y, 2.0f).ShouldBeTrue();
        }

        buffer.IsRectOccluded(0, 0, buffer.Width, buffer.Height, 1.0f).ShouldBeTrue();
        buffer.IsRectOccluded(0, 0, buffer.Width, buffer.Height, 3.0f).ShouldBeFalse();
    }

    [Test]
    public void AabbTesterKeepsRectVisibleWhenCoverageHasHole()
    {
        MaskedOcclusionBuffer buffer = new();
        buffer.Resize(16, 8);
        buffer.Clear();

        Matrix4x4 projection = MakeReciprocalZProjection();
        AABB query = new(new Vector3(-0.5f, -0.5f, 2.0f), new Vector3(0.5f, 0.5f, 2.0f));
        MaskedOcclusionAabbTester.TryProjectAabb(query, projection, buffer.Width, buffer.Height, out ProjectedAabb projected)
            .ShouldBeTrue();

        for (int y = projected.MinY; y < projected.MaxYExclusive; y++)
        {
            for (int x = projected.MinX; x < projected.MaxXExclusive; x++)
            {
                if (x == projected.MinX && y == projected.MinY)
                    continue;
                buffer.TryWritePixel(x, y, 1.0f);
            }
        }

        MaskedOcclusionAabbTester tester = new();
        tester.TestVisible(buffer, projection, query).ShouldBeTrue();
    }

    [Test]
    public void RasterizedOpaqueMeshOccludesAabbBehindIt()
    {
        MaskedOcclusionBuffer buffer = new();
        buffer.Resize(32, 16);
        buffer.Clear();

        XRMesh occluder = XRMesh.CreateTriangles(
            new Vector3(-1.0f, -1.0f, 1.0f),
            new Vector3(1.0f, -1.0f, 1.0f),
            new Vector3(1.0f, 1.0f, 1.0f),
            new Vector3(-1.0f, -1.0f, 1.0f),
            new Vector3(1.0f, 1.0f, 1.0f),
            new Vector3(-1.0f, 1.0f, 1.0f));

        Matrix4x4 projection = MakeReciprocalZProjection();
        MaskedOcclusionRasterizer rasterizer = new();
        int rasterized = rasterizer.RasterizeMesh(
            buffer,
            occluder,
            Matrix4x4.Identity,
            projection,
            new RenderingParameters { CullMode = ECullMode.None },
            triangleBudget: 2);

        rasterized.ShouldBeGreaterThan(0);

        AABB behind = new(new Vector3(0.25f, -0.75f, 2.0f), new Vector3(0.75f, -0.25f, 2.0f));
        MaskedOcclusionAabbTester tester = new();
        tester.TestVisible(buffer, projection, behind).ShouldBeFalse();
    }

    [Test]
    public void CoveredRectOccludesAcrossTileBoundary()
    {
        MaskedOcclusionBuffer buffer = new();
        buffer.Resize(16, 8);
        buffer.Clear();

        for (int y = 0; y < 8; y++)
        {
            for (int x = 4; x < 12; x++)
                buffer.TryWritePixel(x, y, 0.5f);
        }

        buffer.IsRectOccluded(4, 0, 12, 8, 0.25f).ShouldBeTrue();
    }

    [Test]
    public void FartherPixelInCoveredRectKeepsQueryVisible()
    {
        MaskedOcclusionBuffer buffer = new();
        buffer.Resize(8, 4);
        buffer.Clear();

        for (int y = 0; y < buffer.Height; y++)
        {
            for (int x = 0; x < buffer.Width; x++)
                buffer.TryWritePixel(x, y, 0.1f);
        }

        buffer.TryWritePixel(3, 2, 0.5f);

        buffer.IsRectOccluded(0, 0, buffer.Width, buffer.Height, 0.25f).ShouldBeFalse();
    }

    [Test]
    public void AabbTesterKeepsInvalidAndNearClippedBoundsVisible()
    {
        MaskedOcclusionBuffer buffer = CreateFilledBuffer(8, 4, 0.5f);
        MaskedOcclusionAabbTester tester = new();
        Matrix4x4 projection = MakeReciprocalZProjection();

        tester.TestVisible(buffer, projection, default).ShouldBeTrue();

        AABB nearClipped = new(new Vector3(-0.5f, -0.5f, 0.0f), new Vector3(0.5f, 0.5f, 0.0f));
        tester.TestVisible(buffer, projection, nearClipped).ShouldBeTrue();
    }

    [Test]
    public void AabbTesterRejectsFullyOutsideFrustumBounds()
    {
        MaskedOcclusionBuffer buffer = CreateFilledBuffer(8, 4, 0.5f);
        MaskedOcclusionAabbTester tester = new();
        Matrix4x4 projection = MakeReciprocalZProjection();

        AABB outside = new(new Vector3(4.0f, -0.5f, 2.0f), new Vector3(5.0f, 0.5f, 2.0f));

        tester.TestVisible(buffer, projection, outside).ShouldBeFalse();
    }

    [Test]
    public void SafeOpaqueRenderOptionsCanBeOccluders()
    {
        RenderingParameters options = new();

        CpuSoftwareOcclusionCuller.IsRenderOptionsOccluderSafe(options).ShouldBeTrue();
        CpuSoftwareOcclusionCuller.IsRenderOptionsOccluderSafe(null).ShouldBeTrue();
    }

    [Test]
    public void UnsafeRenderOptionsAreRejectedAsOccluders()
    {
        CpuSoftwareOcclusionCuller.IsRenderOptionsOccluderSafe(new RenderingParameters
        {
            BlendModeAllDrawBuffers = BlendMode.EnabledTransparent()
        }).ShouldBeFalse();

        CpuSoftwareOcclusionCuller.IsRenderOptionsOccluderSafe(new RenderingParameters
        {
            BlendModesPerDrawBuffer = new Dictionary<uint, BlendMode>
            {
                [0u] = BlendMode.EnabledTransparent()
            }
        }).ShouldBeFalse();

        CpuSoftwareOcclusionCuller.IsRenderOptionsOccluderSafe(new RenderingParameters
        {
            AlphaToCoverage = ERenderParamUsage.Enabled
        }).ShouldBeFalse();

        CpuSoftwareOcclusionCuller.IsRenderOptionsOccluderSafe(new RenderingParameters
        {
            ExcludeFromCpuOcclusion = true
        }).ShouldBeFalse();

        CpuSoftwareOcclusionCuller.IsRenderOptionsOccluderSafe(new RenderingParameters
        {
            CullMode = ECullMode.Both
        }).ShouldBeFalse();

        CpuSoftwareOcclusionCuller.IsRenderOptionsOccluderSafe(new RenderingParameters
        {
            DepthTest = new DepthTest { Enabled = ERenderParamUsage.Disabled }
        }).ShouldBeFalse();

        CpuSoftwareOcclusionCuller.IsRenderOptionsOccluderSafe(new RenderingParameters
        {
            DepthTest = new DepthTest { UpdateDepth = false }
        }).ShouldBeFalse();

        CpuSoftwareOcclusionCuller.IsRenderOptionsOccluderSafe(new RenderingParameters
        {
            DepthTest = new DepthTest { Function = EComparison.Greater }
        }).ShouldBeFalse();
    }

    private static Matrix4x4 MakeReciprocalZProjection()
    {
        Matrix4x4 projection = Matrix4x4.Identity;
        projection.M34 = 1.0f;
        projection.M44 = 0.0f;
        return projection;
    }

    private static MaskedOcclusionBuffer CreateFilledBuffer(int width, int height, float reciprocalDepth)
    {
        MaskedOcclusionBuffer buffer = new();
        buffer.Resize(width, height);
        buffer.Clear();

        for (int y = 0; y < buffer.Height; y++)
        {
            for (int x = 0; x < buffer.Width; x++)
                buffer.TryWritePixel(x, y, reciprocalDepth);
        }

        return buffer;
    }
}
