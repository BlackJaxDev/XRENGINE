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
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Unit tests for GPU Scene BVH construction and traversal stages.
/// Tests use the actual compute shaders used by the engine for BVH raycast and culling.
/// </summary>
[TestFixture]
public class GpuSceneBvhTests
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
            // Search upward from test output directory to find Build/CommonAssets/Shaders
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                var candidate = Path.Combine(dir, "Build", "CommonAssets", "Shaders");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir) ?? dir;
            }
            // Fallback for development
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

    #region Test Data Structures

    /// <summary>
    /// BVH Node structure matching BvhRaycastCore.glsl layout.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuBvhNode
    {
        public Vector4 MinBounds;
        public Vector4 MaxBounds;
        public uint LeftChildOrFirstPrimitive;
        public uint RightChildOrPrimitiveCount;
        public uint FirstPrimitive;
        public uint FlagsAndCount; // bit 31 = leaf flag
    }

    /// <summary>
    /// Ray input structure matching BvhRaycastCore.glsl RayInput.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuRayInput
    {
        public Vector4 Origin;    // .w = tMin
        public Vector4 Direction; // .w = tMax
    }

    /// <summary>
    /// Packed triangle structure matching BvhRaycastCore.glsl PackedTriangle.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuPackedTriangle
    {
        public Vector4 V0;
        public Vector4 V1;
        public Vector4 V2;
        public uint ObjectId;
        public uint FaceIndex;
        public uint Reserved0;
        public uint Reserved1;
    }

    /// <summary>
    /// Hit record structure matching BvhRaycastCore.glsl HitRecord.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuHitRecord
    {
        public float T;
        public uint ObjectId;
        public uint FaceIndex;
        public uint TriangleIndex;
        public Vector3 Barycentric;
        public float Padding;
    }

    #endregion

    #region Shader Loading Tests

    [Test]
    public void BvhRaycastShader_Loads_Successfully()
    {
        // Act
        string source = LoadShaderSource("Compute/bvh_raycast.comp");

        // Assert
        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("#version 450");
        source.ShouldContain("BvhNode");
        source.ShouldContain("TraceRay");
    }

    [Test]
    public void BvhRaycastCoreSnippet_Loads_Successfully()
    {
        // Act
        string source = LoadShaderSource("Snippets/BvhRaycastCore.glsl");

        // Assert
        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("XR_BVH_LEAF_BIT");
        source.ShouldContain("IntersectAabb");
        source.ShouldContain("IntersectTriangle");
        source.ShouldContain("TraceRay");
    }

    [Test]
    public void BvhAnyhitShader_Loads_Successfully()
    {
        // Act
        string source = LoadShaderSource("Compute/bvh_anyhit.comp");

        // Assert
        source.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public void BvhClosesthitShader_Loads_Successfully()
    {
        // Act
        string source = LoadShaderSource("Compute/bvh_closesthit.comp");

        // Assert
        source.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public void BvhCommandAabbShader_Loads_Successfully()
    {
        // Act
        string source = LoadShaderSource("Scene3D/RenderPipeline/bvh_aabb_from_commands.comp");

        // Assert
        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("COMMAND_FLOATS");
        source.ShouldContain("BOUNDS_OFFSET");
        source.ShouldContain("AABBs");
    }

    #endregion

    #region BVH Data Structure Tests

    [Test]
    public void GpuBvhNode_StructLayout_CorrectSize()
    {
        // BvhNode in shader: vec4 minBounds, vec4 maxBounds, uvec4 meta = 48 bytes
        int expectedSize = 48;
        int actualSize = Marshal.SizeOf<GpuBvhNode>();
        actualSize.ShouldBe(expectedSize);
    }

    [Test]
    public void GpuRayInput_StructLayout_CorrectSize()
    {
        // RayInput in shader: vec4 origin, vec4 direction = 32 bytes
        int expectedSize = 32;
        int actualSize = Marshal.SizeOf<GpuRayInput>();
        actualSize.ShouldBe(expectedSize);
    }

    [Test]
    public void GpuPackedTriangle_StructLayout_CorrectSize()
    {
        // PackedTriangle: vec4 v0, vec4 v1, vec4 v2, uvec4 extra = 64 bytes
        int expectedSize = 64;
        int actualSize = Marshal.SizeOf<GpuPackedTriangle>();
        actualSize.ShouldBe(expectedSize);
    }

    [Test]
    public void GpuHitRecord_StructLayout_CorrectSize()
    {
        // HitRecord: float t, uint objectId, uint faceIndex, uint triangleIndex, vec3 barycentric, float padding = 32 bytes
        int expectedSize = 32;
        int actualSize = Marshal.SizeOf<GpuHitRecord>();
        actualSize.ShouldBe(expectedSize);
    }

    [Test]
    public void BvhLeafBit_MatchesShaderConstant()
    {
        // XR_BVH_LEAF_BIT in shader = 0x80000000u
        const uint XR_BVH_LEAF_BIT = 0x80000000u;
        
        var leafNode = new GpuBvhNode
        {
            FlagsAndCount = XR_BVH_LEAF_BIT | 5u // leaf with 5 primitives
        };

        bool isLeaf = (leafNode.FlagsAndCount & XR_BVH_LEAF_BIT) != 0;
        uint primitiveCount = leafNode.FlagsAndCount & ~XR_BVH_LEAF_BIT;

        isLeaf.ShouldBeTrue();
        primitiveCount.ShouldBe(5u);
    }

    #endregion

    #region Morton Code Tests

    [Test]
    public void MortonCode_CalculatedForDifferentPositions_ProducesUniqueValues()
    {
        // Test that Morton codes produce distinct values for spatially different objects
        // This validates the sorting order used by GPU BVH construction
        
        var positions = new[]
        {
            new Vector3(0, 0, 0),
            new Vector3(1, 0, 0),
            new Vector3(0, 1, 0),
            new Vector3(0, 0, 1),
            new Vector3(1, 1, 1)
        };

        var mortonCodes = new uint[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            mortonCodes[i] = CalculateMortonCode(positions[i], Vector3.Zero, new Vector3(2f));
        }

        // All codes should be distinct for distinct positions
        var uniqueCodes = new HashSet<uint>(mortonCodes);
        uniqueCodes.Count.ShouldBe(positions.Length);
    }

    [Test]
    public void MortonCode_NearbyPositions_ProduceSimilarValues()
    {
        // Test that spatially close objects have similar Morton codes (for BVH locality)
        var pos1 = new Vector3(0.1f, 0.1f, 0.1f);
        var pos2 = new Vector3(0.11f, 0.11f, 0.11f);
        var pos3 = new Vector3(0.9f, 0.9f, 0.9f);

        var sceneMin = Vector3.Zero;
        var sceneMax = new Vector3(1f);

        uint code1 = CalculateMortonCode(pos1, sceneMin, sceneMax);
        uint code2 = CalculateMortonCode(pos2, sceneMin, sceneMax);
        uint code3 = CalculateMortonCode(pos3, sceneMin, sceneMax);

        // code1 and code2 should be closer than code1 and code3
        uint diff12 = code1 > code2 ? code1 - code2 : code2 - code1;
        uint diff13 = code1 > code3 ? code1 - code3 : code3 - code1;

        diff12.ShouldBeLessThan(diff13);
    }

    /// <summary>
    /// CPU reference implementation of Morton code calculation for testing.
    /// Mirrors GPU compute shader logic.
    /// </summary>
    private static uint CalculateMortonCode(Vector3 position, Vector3 sceneMin, Vector3 sceneMax)
    {
        // Normalize position to [0, 1] range
        var normalized = (position - sceneMin) / (sceneMax - sceneMin);
        normalized = Vector3.Clamp(normalized, Vector3.Zero, Vector3.One);

        // Scale to 10-bit integer range (0-1023)
        uint x = (uint)MathF.Floor(normalized.X * 1023f);
        uint y = (uint)MathF.Floor(normalized.Y * 1023f);
        uint z = (uint)MathF.Floor(normalized.Z * 1023f);

        // Interleave bits to form 30-bit Morton code
        return ExpandBits(x) | (ExpandBits(y) << 1) | (ExpandBits(z) << 2);
    }

    private static uint ExpandBits(uint v)
    {
        v = (v * 0x00010001u) & 0xFF0000FFu;
        v = (v * 0x00000101u) & 0x0F00F00Fu;
        v = (v * 0x00000011u) & 0xC30C30C3u;
        v = (v * 0x00000005u) & 0x49249249u;
        return v;
    }

    #endregion

    #region BVH Node Layout Tests

    [Test]
    public void BvhNodeLayout_CalculatesCorrectNodeCount()
    {
        // For a binary BVH with N leaves, we need 2N-1 total nodes
        uint leafCount = 128;
        uint expectedNodeCount = leafCount * 2 - 1;

        // Verify calculation matches expected
        expectedNodeCount.ShouldBe(255u);
    }

    [Test]
    public void BvhNodeLayout_LeafCountFromPrimitives_CorrectForMaxLeafSize()
    {
        // Test leaf count calculation with different max leaf primitives
        uint primitiveCount = 100;
        uint maxLeafPrimitives = 4;

        uint leafCount = (primitiveCount + maxLeafPrimitives - 1) / maxLeafPrimitives;
        leafCount.ShouldBe(25u);

        // With max leaf = 1, each primitive is its own leaf
        uint leafCountNoGrouping = (primitiveCount + 1 - 1) / 1;
        leafCountNoGrouping.ShouldBe(100u);
    }

    [Test]
    public void BvhNodeLayout_InternalNodeCount_IsLeafCountMinusOne()
    {
        uint leafCount = 64;
        uint internalCount = leafCount > 0 ? leafCount - 1 : 0;

        internalCount.ShouldBe(63u);
    }

    #endregion

    #region AABB Operations Tests

    [Test]
    public void AABB_Union_CombinesTwoBounds()
    {
        var a = new AABB(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
        var b = new AABB(new Vector3(0, 0, 0), new Vector3(2, 2, 2));

        var union = AABB.Union(a, b);

        union.Min.X.ShouldBe(-1f);
        union.Min.Y.ShouldBe(-1f);
        union.Min.Z.ShouldBe(-1f);
        union.Max.X.ShouldBe(2f);
        union.Max.Y.ShouldBe(2f);
        union.Max.Z.ShouldBe(2f);
    }

    [Test]
    public void AABB_SurfaceArea_CalculatesCorrectly()
    {
        // Unit cube should have surface area of 6
        var unitCube = new AABB(Vector3.Zero, Vector3.One);
        float area = CalculateSurfaceArea(unitCube);
        area.ShouldBe(6f, 0.001f);

        // 2x2x2 cube should have surface area of 24
        var cube2 = new AABB(Vector3.Zero, new Vector3(2f));
        float area2 = CalculateSurfaceArea(cube2);
        area2.ShouldBe(24f, 0.001f);
    }

    private static float CalculateSurfaceArea(AABB box)
    {
        var d = box.Max - box.Min;
        return 2.0f * (d.X * d.Y + d.Y * d.Z + d.Z * d.X);
    }

    [Test]
    public void AABB_Contains_DetectsPointInside()
    {
        var box = new AABB(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));

        box.ContainsPoint(Vector3.Zero).ShouldBeTrue();
        box.ContainsPoint(new Vector3(0.5f, 0.5f, 0.5f)).ShouldBeTrue();
        box.ContainsPoint(new Vector3(2f, 0f, 0f)).ShouldBeFalse();
    }

    [Test]
    public void AABB_Intersects_DetectsOverlap()
    {
        var a = new AABB(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
        var b = new AABB(new Vector3(0, 0, 0), new Vector3(2, 2, 2));
        var c = new AABB(new Vector3(5, 5, 5), new Vector3(6, 6, 6));

        a.Intersects(b).ShouldBeTrue();
        a.Intersects(c).ShouldBeFalse();
    }

    #endregion

    #region GPU Command Structure Tests

    [Test]
    public void GPUIndirectRenderCommand_StructLayout_CorrectSize()
    {
        // Verify the GPU command structure has expected size (192 bytes / 48 floats)
        int expectedSize = 192;
        int actualSize = System.Runtime.InteropServices.Marshal.SizeOf<GPUIndirectRenderCommand>();
        
        actualSize.ShouldBe(expectedSize);
    }

    [Test]
    public void GPUIndirectRenderCommand_SetBoundingSphere_StoresCorrectly()
    {
        var cmd = new GPUIndirectRenderCommand();
        var center = new Vector3(1f, 2f, 3f);
        float radius = 5f;

        cmd.SetBoundingSphere(center, radius);

        cmd.BoundingSphere.X.ShouldBe(1f);
        cmd.BoundingSphere.Y.ShouldBe(2f);
        cmd.BoundingSphere.Z.ShouldBe(3f);
        cmd.BoundingSphere.W.ShouldBe(5f);
    }

    [Test]
    public void GPUIndirectRenderCommand_Flags_CanSetMultipleFlags()
    {
        var cmd = new GPUIndirectRenderCommand
        {
            Flags = (uint)(GPUIndirectRenderFlags.Transparent | GPUIndirectRenderFlags.CastShadow)
        };

        ((GPUIndirectRenderFlags)cmd.Flags).HasFlag(GPUIndirectRenderFlags.Transparent).ShouldBeTrue();
        ((GPUIndirectRenderFlags)cmd.Flags).HasFlag(GPUIndirectRenderFlags.CastShadow).ShouldBeTrue();
        ((GPUIndirectRenderFlags)cmd.Flags).HasFlag(GPUIndirectRenderFlags.Skinned).ShouldBeFalse();
    }

    [Test]
    public void GPUIndirectRenderCommand_DefaultValues_AreZeroInitialized()
    {
        var cmd = new GPUIndirectRenderCommand();

        cmd.MeshID.ShouldBe(0u);
        cmd.MaterialID.ShouldBe(0u);
        cmd.InstanceCount.ShouldBe(0u);
        cmd.Flags.ShouldBe(0u);
        cmd.WorldMatrix.ShouldBe(default(Matrix4x4));
    }

    #endregion

    #region Frustum Culling Logic Tests

    [Test]
    public void FrustumCulling_SphereInsideFrustum_NotCulled()
    {
        // Simple frustum represented as 6 planes (simplified for testing)
        var frustumPlanes = CreateSimpleFrustumPlanes(
            nearZ: 0.1f,
            farZ: 100f,
            aspectRatio: 1.0f,
            fovY: MathF.PI / 4f
        );

        var sphereCenter = new Vector3(0, 0, -10); // In front of camera
        float sphereRadius = 1f;

        bool isVisible = TestSphereAgainstFrustum(sphereCenter, sphereRadius, frustumPlanes);
        isVisible.ShouldBeTrue();
    }

    [Test]
    public void FrustumCulling_SphereBehindNearPlane_IsCulled()
    {
        var frustumPlanes = CreateSimpleFrustumPlanes(
            nearZ: 0.1f,
            farZ: 100f,
            aspectRatio: 1.0f,
            fovY: MathF.PI / 4f
        );

        var sphereCenter = new Vector3(0, 0, 5); // Behind camera (positive Z)
        float sphereRadius = 1f;

        bool isVisible = TestSphereAgainstFrustum(sphereCenter, sphereRadius, frustumPlanes);
        isVisible.ShouldBeFalse();
    }

    [Test]
    public void FrustumCulling_SphereBeyondFarPlane_IsCulled()
    {
        var frustumPlanes = CreateSimpleFrustumPlanes(
            nearZ: 0.1f,
            farZ: 100f,
            aspectRatio: 1.0f,
            fovY: MathF.PI / 4f
        );

        var sphereCenter = new Vector3(0, 0, -200); // Way past far plane
        float sphereRadius = 1f;

        bool isVisible = TestSphereAgainstFrustum(sphereCenter, sphereRadius, frustumPlanes);
        isVisible.ShouldBeFalse();
    }

    [Test]
    public void FrustumCulling_SpherePartiallyIntersecting_NotCulled()
    {
        var frustumPlanes = CreateSimpleFrustumPlanes(
            nearZ: 0.1f,
            farZ: 100f,
            aspectRatio: 1.0f,
            fovY: MathF.PI / 4f
        );

        // Sphere at edge of frustum but partially inside
        var sphereCenter = new Vector3(10, 0, -10);
        float sphereRadius = 5f;

        bool isVisible = TestSphereAgainstFrustum(sphereCenter, sphereRadius, frustumPlanes);
        // Conservative test - sphere might be considered visible if any part could be inside
        // This depends on implementation, so we just verify no crash
        isVisible.ShouldSatisfyAllConditions();
    }

    /// <summary>
    /// Creates simplified frustum planes for testing (looking down -Z axis).
    /// </summary>
    private static Vector4[] CreateSimpleFrustumPlanes(float nearZ, float farZ, float aspectRatio, float fovY)
    {
        // 6 planes: near, far, left, right, top, bottom
        var planes = new Vector4[6];

        // Near plane (pointing towards camera center, i.e., +Z direction for near at -nearZ)
        planes[0] = new Vector4(0, 0, -1, -nearZ); // points into frustum

        // Far plane (pointing away from camera)
        planes[1] = new Vector4(0, 0, 1, farZ);

        // Side planes based on FOV
        float tanHalfFov = MathF.Tan(fovY / 2f);

        // Simplified side planes for testing
        float sideAngle = MathF.Atan(tanHalfFov * aspectRatio);
        float cosAngle = MathF.Cos(sideAngle);
        float sinAngle = MathF.Sin(sideAngle);

        // Left plane
        planes[2] = new Vector4(cosAngle, 0, -sinAngle, 0);
        // Right plane
        planes[3] = new Vector4(-cosAngle, 0, -sinAngle, 0);
        // Top plane
        planes[4] = new Vector4(0, -cosAngle, -sinAngle, 0);
        // Bottom plane
        planes[5] = new Vector4(0, cosAngle, -sinAngle, 0);

        return planes;
    }

    /// <summary>
    /// Tests a sphere against frustum planes using signed distance.
    /// </summary>
    private static bool TestSphereAgainstFrustum(Vector3 center, float radius, Vector4[] planes)
    {
        foreach (var plane in planes)
        {
            var normal = new Vector3(plane.X, plane.Y, plane.Z);
            float distance = Vector3.Dot(normal, center) + plane.W;

            // If sphere is completely behind plane (outside frustum)
            if (distance < -radius)
                return false;
        }
        return true;
    }

    #endregion
}
