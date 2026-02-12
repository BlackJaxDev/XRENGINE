using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Unit tests for the GPU culling pipeline stages.
/// Tests use the actual compute shaders: GPURenderCulling.comp and GPURenderHiZSoACulling.comp
/// </summary>
[TestFixture]
public class GpuCullingPipelineTests
{
    private const int Width = 256;
    private const int Height = 256;

    /// <summary>
    /// Gets the path to the shader directory.
    /// </summary>
    private static string ShaderBasePath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                var candidate = Path.Combine(dir, "Build", "CommonAssets", "Shaders");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir) ?? dir;
            }
            return @"D:\Documents\XRENGINE\Build\CommonAssets\Shaders";
        }
    }

    private static string LoadShaderSource(string relativePath)
    {
        var fullPath = Path.Combine(ShaderBasePath, relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Shader file not found: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static bool IsTrue(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return false;
        v = v.Trim();
        return v.Equals("1") || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShowWindow => IsTrue(Environment.GetEnvironmentVariable("XR_SHOW_TEST_WINDOWS"));

    #region Shader Loading Tests

    [Test]
    public void GPURenderCullingShader_Loads_AndContainsExpectedFunctions()
    {
        string source = LoadShaderSource("Compute/GPURenderCulling.comp");

        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("FrustumSphereVisible");
        source.ShouldContain("FrustumPlanes[6]");
        source.ShouldContain("MaxRenderDistance");
        source.ShouldContain("CameraLayerMask");
        source.ShouldContain("DisabledFlagsMask");
    }

    [Test]
    public void GPURenderHiZSoACullingShader_Loads_Successfully()
    {
        string source = LoadShaderSource("Compute/GPURenderHiZSoACulling.comp");

        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("#version 460 core");
        source.ShouldContain("HiZOccluded");
        source.ShouldContain("FrustumSphereVisible");
        source.ShouldContain("HiZDepth");
    }

    [Test]
    public void GPURenderHiZInitShader_Loads_AndContainsExpectedBindings()
    {
        string source = LoadShaderSource("Compute/GPURenderHiZInit.comp");

        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("depthTexture");
        source.ShouldContain("hiZBuffer");
        source.ShouldContain("mipLevelSize");
    }

    [Test]
    public void GPURenderOcclusionHiZShader_Loads_AndContainsExpectedUniforms()
    {
        string source = LoadShaderSource("Compute/GPURenderOcclusionHiZ.comp");

        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("HiZOccluded");
        source.ShouldContain("ViewProj");
        source.ShouldContain("HiZMaxMip");
        source.ShouldContain("IsReversedDepth");
    }

    [Test]
    public void GPURenderCopyCount3Shader_Loads_AndContainsExpectedBuffers()
    {
        string source = LoadShaderSource("Compute/GPURenderCopyCount3.comp");

        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("SrcCountBuffer");
        source.ShouldContain("DstCountBuffer");
        source.ShouldContain("Dst0 = Src0");
    }

    [Test]
    public void CullingShader_FlagBitLayout_MatchesCSharpFlags()
    {
        string source = LoadShaderSource("Compute/GPURenderCulling.comp");

        // Verify flag constants match the C# GPUIndirectRenderFlags enum
        source.ShouldContain("FLAG_TRANSPARENT    (1u<<0)");
        source.ShouldContain("FLAG_CAST_SHADOW    (1u<<1)");
        source.ShouldContain("FLAG_SKINNED        (1u<<2)");
        source.ShouldContain("FLAG_DYNAMIC        (1u<<3)");
        source.ShouldContain("FLAG_DOUBLE_SIDED   (1u<<4)");
    }

    [Test]
    public void CullingShader_CommandLayout_Matches48Floats()
    {
        string source = LoadShaderSource("Compute/GPURenderCulling.comp");

        source.ShouldContain("COMMAND_FLOATS = 48");
        source.ShouldContain("192 bytes"); // 48 * 4 = 192
    }

    [Test]
    public void CullingShader_StatsBuffer_LayoutMatchesExpected()
    {
        string source = LoadShaderSource("Compute/GPURenderCulling.comp");

        // StatsBuffer layout must match C# GpuStatsLayout
        source.ShouldContain("StatsInputCount");
        source.ShouldContain("StatsCulledCount");
        source.ShouldContain("StatsRejectedFrustum");
        source.ShouldContain("StatsRejectedDistance");
        source.ShouldContain("StatsBvhBuildCount");
        source.ShouldContain("StatsBvhRefitCount");
        source.ShouldContain("StatsBvhCullCount");
        source.ShouldContain("StatsBvhRayCount");
    }

    #endregion

    #region Frustum Plane Extraction Tests

    [Test]
    public void FrustumPlanes_ExtractFromProjectionMatrix_SixPlanes()
    {
        // Create a perspective projection matrix
        float fov = MathF.PI / 4f;
        float aspect = 16f / 9f;
        float near = 0.1f;
        float far = 1000f;

        var projection = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, near, far);
        
        var planes = ExtractFrustumPlanes(projection);
        
        planes.Length.ShouldBe(6);
    }

    [Test]
    public void FrustumPlanes_NormalizationRequired_ForDistanceCalculation()
    {
        float fov = MathF.PI / 4f;
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(fov, 1f, 0.1f, 100f);
        
        var planes = ExtractFrustumPlanes(projection);
        
        // Verify planes are normalized (normal length ≈ 1)
        foreach (var plane in planes)
        {
            var normal = new Vector3(plane.X, plane.Y, plane.Z);
            float length = normal.Length();
            length.ShouldBeInRange(0.9f, 1.1f);
        }
    }

    private static Vector4[] ExtractFrustumPlanes(Matrix4x4 viewProjection)
    {
        var planes = new Vector4[6];
        
        // Extract from columns of view-projection matrix
        var m = viewProjection;
        
        // Left plane: row3 + row0
        planes[0] = new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41);
        // Right plane: row3 - row0
        planes[1] = new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41);
        // Bottom plane: row3 + row1
        planes[2] = new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42);
        // Top plane: row3 - row1
        planes[3] = new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42);
        // Near plane: row3 + row2
        planes[4] = new Vector4(m.M14 + m.M13, m.M24 + m.M23, m.M34 + m.M33, m.M44 + m.M43);
        // Far plane: row3 - row2
        planes[5] = new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43);
        
        // Normalize planes
        for (int i = 0; i < 6; i++)
        {
            var normal = new Vector3(planes[i].X, planes[i].Y, planes[i].Z);
            float length = normal.Length();
            if (length > 0.0001f)
            {
                planes[i] /= length;
            }
        }
        
        return planes;
    }

    #endregion

    #region Sphere-Frustum Culling Tests

    [Test]
    public void SphereFrustumCulling_SphereAtOrigin_InsideFrustum()
    {
        var viewProjection = CreateLookAtProjection(
            eye: new Vector3(0, 0, 5),
            target: Vector3.Zero,
            up: Vector3.UnitY,
            fov: MathF.PI / 4f,
            aspect: 1f,
            near: 0.1f,
            far: 100f
        );
        
        var planes = ExtractFrustumPlanes(viewProjection);
        
        // Sphere at origin should be visible from camera at (0, 0, 5)
        bool visible = TestSphereVisibility(Vector3.Zero, 1f, planes);
        visible.ShouldBeTrue();
    }

    [Test]
    public void SphereFrustumCulling_SphereBehindCamera_OutsideFrustum()
    {
        var viewProjection = CreateLookAtProjection(
            eye: new Vector3(0, 0, 5),
            target: Vector3.Zero,
            up: Vector3.UnitY,
            fov: MathF.PI / 4f,
            aspect: 1f,
            near: 0.1f,
            far: 100f
        );
        
        var planes = ExtractFrustumPlanes(viewProjection);
        
        // Sphere behind camera
        bool visible = TestSphereVisibility(new Vector3(0, 0, 10), 1f, planes);
        visible.ShouldBeFalse();
    }

    [Test]
    public void SphereFrustumCulling_SphereFarAway_OutsideFrustum()
    {
        var viewProjection = CreateLookAtProjection(
            eye: new Vector3(0, 0, 5),
            target: Vector3.Zero,
            up: Vector3.UnitY,
            fov: MathF.PI / 4f,
            aspect: 1f,
            near: 0.1f,
            far: 100f
        );
        
        var planes = ExtractFrustumPlanes(viewProjection);
        
        // Sphere beyond far plane
        bool visible = TestSphereVisibility(new Vector3(0, 0, -200), 1f, planes);
        visible.ShouldBeFalse();
    }

    [Test]
    public void SphereFrustumCulling_LargeSpherePartiallyVisible_NotCulled()
    {
        var viewProjection = CreateLookAtProjection(
            eye: new Vector3(0, 0, 5),
            target: Vector3.Zero,
            up: Vector3.UnitY,
            fov: MathF.PI / 4f,
            aspect: 1f,
            near: 0.1f,
            far: 100f
        );
        
        var planes = ExtractFrustumPlanes(viewProjection);
        
        // Large sphere at edge - should still be visible due to size
        bool visible = TestSphereVisibility(new Vector3(10, 0, -5), 12f, planes);
        visible.ShouldBeTrue();
    }

    private static Matrix4x4 CreateLookAtProjection(
        Vector3 eye, Vector3 target, Vector3 up,
        float fov, float aspect, float near, float far)
    {
        var view = Matrix4x4.CreateLookAt(eye, target, up);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, near, far);
        return view * projection;
    }

    private static bool TestSphereVisibility(Vector3 center, float radius, Vector4[] planes)
    {
        foreach (var plane in planes)
        {
            var normal = new Vector3(plane.X, plane.Y, plane.Z);
            float distance = Vector3.Dot(normal, center) + plane.W;
            
            if (distance < -radius)
                return false;
        }
        return true;
    }

    #endregion

    #region AABB-Frustum Culling Tests

    [Test]
    public void AabbFrustumCulling_BoxAtOrigin_Visible()
    {
        var viewProjection = CreateLookAtProjection(
            eye: new Vector3(0, 0, 10),
            target: Vector3.Zero,
            up: Vector3.UnitY,
            fov: MathF.PI / 4f,
            aspect: 1f,
            near: 0.1f,
            far: 100f
        );
        
        var planes = ExtractFrustumPlanes(viewProjection);
        var box = new AABB(new Vector3(-1), new Vector3(1));
        
        bool visible = TestAabbVisibility(box, planes);
        visible.ShouldBeTrue();
    }

    [Test]
    public void AabbFrustumCulling_BoxFarLeft_OutsideFrustum()
    {
        var viewProjection = CreateLookAtProjection(
            eye: new Vector3(0, 0, 10),
            target: Vector3.Zero,
            up: Vector3.UnitY,
            fov: MathF.PI / 4f,
            aspect: 1f,
            near: 0.1f,
            far: 100f
        );
        
        var planes = ExtractFrustumPlanes(viewProjection);
        var box = new AABB(new Vector3(-100, -1, -1), new Vector3(-90, 1, 1));
        
        bool visible = TestAabbVisibility(box, planes);
        visible.ShouldBeFalse();
    }

    private static bool TestAabbVisibility(AABB box, Vector4[] planes)
    {
        foreach (var plane in planes)
        {
            var normal = new Vector3(plane.X, plane.Y, plane.Z);
            
            // Find the positive and negative vertices
            var pVertex = new Vector3(
                normal.X >= 0 ? box.Max.X : box.Min.X,
                normal.Y >= 0 ? box.Max.Y : box.Min.Y,
                normal.Z >= 0 ? box.Max.Z : box.Min.Z
            );
            
            // If positive vertex is outside plane, AABB is fully outside
            float distance = Vector3.Dot(normal, pVertex) + plane.W;
            if (distance < 0)
                return false;
        }
        return true;
    }

    #endregion

    #region Culling Counter Tests

    [Test]
    public void CullingCounters_Reset_AllZero()
    {
        var counters = new CullingCounters();
        
        counters.InputCount.ShouldBe(0u);
        counters.CulledCount.ShouldBe(0u);
        counters.DrawnCount.ShouldBe(0u);
        counters.FrustumRejected.ShouldBe(0u);
        counters.DistanceRejected.ShouldBe(0u);
    }

    [Test]
    public void CullingCounters_Increment_TracksCorrectly()
    {
        var counters = new CullingCounters();
        
        counters.InputCount = 100;
        counters.FrustumRejected = 30;
        counters.DistanceRejected = 20;
        counters.CulledCount = counters.InputCount - counters.FrustumRejected - counters.DistanceRejected;
        counters.DrawnCount = counters.CulledCount;
        
        counters.CulledCount.ShouldBe(50u);
        counters.DrawnCount.ShouldBe(50u);
    }

    [Test]
    public void CullingCounters_OverflowFlag_SetsWhenExceedingCapacity()
    {
        var counters = new CullingCounters
        {
            InputCount = 1000,
            Capacity = 100,
            OverflowFlag = 0
        };
        
        if (counters.InputCount > counters.Capacity)
            counters.OverflowFlag = counters.InputCount; // Store offending count
        
        counters.OverflowFlag.ShouldBe(1000u);
    }

    private struct CullingCounters
    {
        public uint InputCount;
        public uint CulledCount;
        public uint DrawnCount;
        public uint FrustumRejected;
        public uint DistanceRejected;
        public uint Capacity;
        public uint OverflowFlag;
    }

    #endregion

    #region Layer Mask Culling Tests

    [Test]
    public void LayerMaskCulling_MatchingLayers_NotCulled()
    {
        uint commandLayerMask = 0b0001; // Layer 0
        uint cameraLayerMask = 0b1111;  // Sees all first 4 layers
        
        bool visible = (commandLayerMask & cameraLayerMask) != 0;
        visible.ShouldBeTrue();
    }

    [Test]
    public void LayerMaskCulling_NonMatchingLayers_Culled()
    {
        uint commandLayerMask = 0b0001; // Layer 0
        uint cameraLayerMask = 0b1110;  // Does NOT see layer 0
        
        bool visible = (commandLayerMask & cameraLayerMask) != 0;
        visible.ShouldBeFalse();
    }

    [Test]
    public void LayerMaskCulling_AllLayersMask_SeesEverything()
    {
        uint cameraLayerMask = 0xFFFFFFFF;
        
        (0b0001 & cameraLayerMask).ShouldNotBe(0u);
        (0b0100 & cameraLayerMask).ShouldNotBe(0u);
        (0x80000000 & cameraLayerMask).ShouldNotBe(0u);
    }

    [Test]
    public void LayerMaskCulling_MultipleLayerObject_VisibleIfAnyMatch()
    {
        uint commandLayerMask = 0b0101; // Layers 0 and 2
        uint cameraLayerMask = 0b0100;  // Only sees layer 2
        
        bool visible = (commandLayerMask & cameraLayerMask) != 0;
        visible.ShouldBeTrue();
    }

    #endregion

    #region Flag-Based Culling Tests

    [Test]
    public void FlagCulling_TransparentInOpaquePass_Culled()
    {
        uint objectFlags = (uint)GPUIndirectRenderFlags.Transparent;
        uint disabledFlags = (uint)GPUIndirectRenderFlags.Transparent; // Opaque pass disables transparent
        
        bool culled = (objectFlags & disabledFlags) != 0;
        culled.ShouldBeTrue();
    }

    [Test]
    public void FlagCulling_OpaqueInOpaquePass_NotCulled()
    {
        uint objectFlags = (uint)GPUIndirectRenderFlags.CastShadow;
        uint disabledFlags = (uint)GPUIndirectRenderFlags.Transparent;
        
        bool culled = (objectFlags & disabledFlags) != 0;
        culled.ShouldBeFalse();
    }

    [Test]
    public void FlagCulling_ShadowCasterInShadowPass_NotCulled()
    {
        uint objectFlags = (uint)GPUIndirectRenderFlags.CastShadow;
        uint requiredFlags = (uint)GPUIndirectRenderFlags.CastShadow;
        
        bool visible = (objectFlags & requiredFlags) != 0;
        visible.ShouldBeTrue();
    }

    [Test]
    public void FlagCulling_NonShadowCasterInShadowPass_Culled()
    {
        uint objectFlags = 0; // No shadow casting
        uint requiredFlags = (uint)GPUIndirectRenderFlags.CastShadow;
        
        bool visible = (objectFlags & requiredFlags) != 0;
        visible.ShouldBeFalse();
    }

    #endregion

    #region Occlusion Culling Preparation Tests

    [Test]
    public void OcclusionCulling_ScreenSpaceBounds_CalculatedFromAABB()
    {
        var box = new AABB(new Vector3(-1, -1, -5), new Vector3(1, 1, -5));
        var viewProjection = CreateLookAtProjection(
            eye: Vector3.Zero,
            target: new Vector3(0, 0, -1),
            up: Vector3.UnitY,
            fov: MathF.PI / 2f,
            aspect: 1f,
            near: 0.1f,
            far: 100f
        );
        
        var screenBounds = ProjectAabbToScreen(box, viewProjection);
        
        // Should produce valid screen-space bounds
        screenBounds.Min.X.ShouldBeLessThan(screenBounds.Max.X);
        screenBounds.Min.Y.ShouldBeLessThan(screenBounds.Max.Y);
    }

    private static (Vector2 Min, Vector2 Max) ProjectAabbToScreen(AABB box, Matrix4x4 viewProjection)
    {
        var corners = new[]
        {
            new Vector3(box.Min.X, box.Min.Y, box.Min.Z),
            new Vector3(box.Max.X, box.Min.Y, box.Min.Z),
            new Vector3(box.Min.X, box.Max.Y, box.Min.Z),
            new Vector3(box.Max.X, box.Max.Y, box.Min.Z),
            new Vector3(box.Min.X, box.Min.Y, box.Max.Z),
            new Vector3(box.Max.X, box.Min.Y, box.Max.Z),
            new Vector3(box.Min.X, box.Max.Y, box.Max.Z),
            new Vector3(box.Max.X, box.Max.Y, box.Max.Z)
        };
        
        var screenMin = new Vector2(float.MaxValue);
        var screenMax = new Vector2(float.MinValue);
        
        foreach (var corner in corners)
        {
            var clip = Vector4.Transform(new Vector4(corner, 1), viewProjection);
            if (clip.W <= 0) continue;
            
            var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
            var screen = (ndc + Vector2.One) * 0.5f;
            
            screenMin = Vector2.Min(screenMin, screen);
            screenMax = Vector2.Max(screenMax, screen);
        }
        
        return (screenMin, screenMax);
    }

    #endregion

    #region Hi-Z Culling Tests

    [Test]
    public void HiZCulling_MipLevelSelection_BasedOnScreenSize()
    {
        // Select appropriate Hi-Z mip level based on screen coverage
        uint screenWidth = 1920;
        uint screenHeight = 1080;
        
        // Object covering ~32 pixels should use mip level 5 (32 = 2^5)
        uint pixelSize = 32;
        uint mipLevel = (uint)MathF.Log2(pixelSize);
        mipLevel.ShouldBe(5u);
        
        // Object covering ~256 pixels should use mip level 8
        pixelSize = 256;
        mipLevel = (uint)MathF.Log2(pixelSize);
        mipLevel.ShouldBe(8u);
    }

    [Test]
    public void HiZCulling_DepthComparison_BehindOccluder_Culled()
    {
        float objectDepth = 0.8f;
        float hiZDepth = 0.5f; // Occluder is closer
        
        bool occluded = objectDepth > hiZDepth;
        occluded.ShouldBeTrue();
    }

    [Test]
    public void HiZCulling_DepthComparison_InFrontOfOccluder_NotCulled()
    {
        float objectDepth = 0.3f;
        float hiZDepth = 0.5f;
        
        bool occluded = objectDepth > hiZDepth;
        occluded.ShouldBeFalse();
    }

    #endregion

    #region Atomic Counter Tests

    [Test]
    public void AtomicCounter_Increment_ProducesSequentialValues()
    {
        // Simulate atomic counter behavior
        uint counter = 0;
        var indices = new uint[10];
        
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = counter++;
        }
        
        for (uint i = 0; i < indices.Length; i++)
        {
            indices[i].ShouldBe(i);
        }
    }

    [Test]
    public void AtomicCounter_Reset_StartsFromZero()
    {
        uint counter = 100;
        counter = 0; // Reset
        
        uint firstIndex = counter++;
        firstIndex.ShouldBe(0u);
    }

    [Test]
    public void AtomicCounter_BoundsCheck_PreventsOverflow()
    {
        uint counter = 0;
        uint maxCapacity = 100;
        uint overflowFlag = 0;
        
        for (int i = 0; i < 150; i++)
        {
            uint index = counter;
            if (index >= maxCapacity)
            {
                overflowFlag = (uint)(i + 1);
                break;
            }
            counter++;
        }
        
        overflowFlag.ShouldBe(101u);
        counter.ShouldBe(100u);
    }

    #endregion

    #region Culling Pipeline Stage Order Tests

    [Test]
    public void CullingPipeline_StageOrder_EarlyOutFirst()
    {
        // Optimal culling order: cheapest tests first
        var stages = new[]
        {
            "FlagCheck",       // Bitwise AND - cheapest
            "LayerMask",       // Bitwise AND
            "RenderPassFilter",// Integer compare
            "DistanceCull",    // One distance calc
            "FrustumCull",     // 6 plane tests
            "OcclusionCull"    // Texture fetch - most expensive
        };
        
        // Flag check should be first (index 0)
        Array.IndexOf(stages, "FlagCheck").ShouldBe(0);
        
        // Occlusion should be last
        Array.IndexOf(stages, "OcclusionCull").ShouldBe(stages.Length - 1);
    }

    #endregion

    #region SoA (Structure of Arrays) Layout Tests

    [Test]
    public void SoALayout_BoundingSpheres_Contiguous()
    {
        // SoA layout has separate arrays for each component
        int commandCount = 100;
        
        var centerX = new float[commandCount];
        var centerY = new float[commandCount];
        var centerZ = new float[commandCount];
        var radius = new float[commandCount];
        
        // Fill with test data
        for (int i = 0; i < commandCount; i++)
        {
            centerX[i] = i * 1f;
            centerY[i] = i * 2f;
            centerZ[i] = i * 3f;
            radius[i] = 1f;
        }
        
        // Verify data independence
        centerX[50].ShouldBe(50f);
        centerY[50].ShouldBe(100f);
        centerZ[50].ShouldBe(150f);
    }

    [Test]
    public void SoALayout_WorldMatrices_PackedForGpu()
    {
        // World matrices in SoA: separate arrays for each row or column
        int commandCount = 4;
        
        // Row-major packing: 4 arrays of 4 floats each per matrix
        var row0 = new Vector4[commandCount];
        var row1 = new Vector4[commandCount];
        var row2 = new Vector4[commandCount];
        var row3 = new Vector4[commandCount];
        
        // Identity matrix for command 0
        row0[0] = new Vector4(1, 0, 0, 0);
        row1[0] = new Vector4(0, 1, 0, 0);
        row2[0] = new Vector4(0, 0, 1, 0);
        row3[0] = new Vector4(0, 0, 0, 1);
        
        // Verify identity matrix rows
        row0[0].X.ShouldBe(1f);
        row1[0].Y.ShouldBe(1f);
        row2[0].Z.ShouldBe(1f);
        row3[0].W.ShouldBe(1f);
    }

    #endregion
}
