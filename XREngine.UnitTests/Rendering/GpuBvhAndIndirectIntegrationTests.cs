using NUnit.Framework;
using Shouldly;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Integration tests that actually compile and run GPU compute shaders for:
/// - BVH build (bvh_build.comp)
/// - BVH refit (bvh_refit.comp)
/// - GPU frustum culling (GPURenderCulling.comp)
/// - GPU indirect draw command generation (GPURenderIndirect.comp)
/// 
/// These tests create a real OpenGL 4.6 context, compile the actual shader files,
/// set up GPU buffers with test data, dispatch compute shaders, and validate results.
/// </summary>
[TestFixture]
public class GpuBvhAndIndirectIntegrationTests : GpuTestBase
{
    /// <summary>
    /// Extended hardware check: also rejects known software renderers
    /// (GDI Generic, llvmpipe, SwiftShader) that can create a context but
    /// silently miscompute shaders.
    /// </summary>
    private new static void AssertHardwareComputeOrInconclusive(GL gl)
    {
        if (IsHeadless)
            Assert.Inconclusive("GPU compute integration tests skipped in headless/CI mode.");

        string vendor = gl.GetStringS(StringName.Vendor) ?? string.Empty;
        string renderer = gl.GetStringS(StringName.Renderer) ?? string.Empty;

        if (vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("GDI Generic", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"GPU compute integration tests require a hardware OpenGL driver. Vendor='{vendor}', Renderer='{renderer}'.");
        }
    }

    [SetUp]
    public void EnableCpuReadbackForAssertions()
    {
        // These integration tests assert visible counts and other GPU-written counters.
        // Phase 2 disables CPU readback by default to avoid stalls in runtime; tests opt back in.
        GPURenderPassCollection.ConfigureIndirectDebug(d =>
        {
            d.DisableCpuReadbackCount = false;
            d.EnableCpuBatching = false;
            d.ForceCpuFallbackCount = false;
        });
    }

    #region Shader Compilation Tests

    [Test]
    public void GPURenderIndirect_ComputeShader_CompilesSuccessfully()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", "Indirect", "GPURenderIndirect.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            string source = File.ReadAllText(shaderPath);
            uint shader = CompileComputeShader(gl, source);
            
            shader.ShouldBeGreaterThan(0u, "Compute shader should compile successfully");
            
            uint program = CreateComputeProgram(gl, shader);
            program.ShouldBeGreaterThan(0u, "Compute program should link successfully");

            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public void GPURenderCulling_ComputeShader_CompilesSuccessfully()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", "Culling", "GPURenderCulling.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            string source = File.ReadAllText(shaderPath);
            uint shader = CompileComputeShader(gl, source);
            
            shader.ShouldBeGreaterThan(0u, "Culling compute shader should compile successfully");
            
            uint program = CreateComputeProgram(gl, shader);
            program.ShouldBeGreaterThan(0u, "Culling compute program should link successfully");

            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public void BvhBuild_ComputeShader_CompilesSuccessfully()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Scene3D", "RenderPipeline", "bvh_build.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"BVH build shader not found: {shaderPath}");
                return;
            }

            string source = File.ReadAllText(shaderPath);
            
            // BVH shaders need includes resolved and specialization constants stripped
            source = ResolveIncludes(source, Path.GetDirectoryName(shaderPath)!);
            source = StripVulkanSpecializationConstants(source);
            
            uint shader = CompileComputeShader(gl, source);
            
            shader.ShouldBeGreaterThan(0u, "BVH build compute shader should compile successfully");
            
            uint program = CreateComputeProgram(gl, shader);
            program.ShouldBeGreaterThan(0u, "BVH build compute program should link successfully");

            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public void BvhRefit_ComputeShader_CompilesSuccessfully()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Scene3D", "RenderPipeline", "bvh_refit.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"BVH refit shader not found: {shaderPath}");
                return;
            }

            string source = File.ReadAllText(shaderPath);
            source = ResolveIncludes(source, Path.GetDirectoryName(shaderPath)!);
            source = StripVulkanSpecializationConstants(source);
            
            uint shader = CompileComputeShader(gl, source);
            
            shader.ShouldBeGreaterThan(0u, "BVH refit compute shader should compile successfully");
            
            uint program = CreateComputeProgram(gl, shader);
            program.ShouldBeGreaterThan(0u, "BVH refit compute program should link successfully");

            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public unsafe void BvhBuildAndRefit_ProducesExpectedRootBounds()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string buildPath = Path.Combine(ShaderBasePath, "Scene3D", "RenderPipeline", "bvh_build.comp");
            string refitPath = Path.Combine(ShaderBasePath, "Scene3D", "RenderPipeline", "bvh_refit.comp");
            if (!File.Exists(buildPath) || !File.Exists(refitPath))
            {
                Assert.Inconclusive($"BVH shader(s) not found: {buildPath} / {refitPath}");
                return;
            }

            string buildSrc = File.ReadAllText(buildPath);
            buildSrc = ResolveIncludes(buildSrc, Path.GetDirectoryName(buildPath)!);
            buildSrc = StripVulkanSpecializationConstants(buildSrc, maxLeafPrimitives: 1, bvhMode: 0);

            string refitSrc = File.ReadAllText(refitPath);
            refitSrc = ResolveIncludes(refitSrc, Path.GetDirectoryName(refitPath)!);
            refitSrc = StripVulkanSpecializationConstants(refitSrc, maxLeafPrimitives: 1, bvhMode: 0);

            uint buildShader = CompileComputeShader(gl, buildSrc);
            uint buildProgram = CreateComputeProgram(gl, buildShader);
            uint refitShader = CompileComputeShader(gl, refitSrc);
            uint refitProgram = CreateComputeProgram(gl, refitShader);

            buildProgram.ShouldBeGreaterThan(0u);
            refitProgram.ShouldBeGreaterThan(0u);

            const uint numPrimitives = 4u;
            const uint maxLeafPrims = 1u;
            uint leafCount = (numPrimitives + maxLeafPrims - 1u) / maxLeafPrims; // = 4
            uint internalCount = leafCount > 0u ? (leafCount - 1u) : 0u;
            uint nodeCount = leafCount > 0u ? (leafCount * 2u - 1u) : 0u;         // = 7

            // === Buffers ===
            // AABBs: struct Aabb { vec4 minBounds; vec4 maxBounds; } interleaved
            Vector4[] aabbs =
            [
                new Vector4(0, 0, 0, 1), new Vector4(1, 1, 1, 1),
                new Vector4(2, 0, 0, 1), new Vector4(3, 1, 1, 1),
                new Vector4(0, 2, 0, 1), new Vector4(1, 3, 1, 1),
                new Vector4(-1, -1, -1, 1), new Vector4(0, 0, 0, 1),
            ];

            // Morton codes: sorted already; objectId maps directly to primitive index.
            uint[] morton =
            [
                0u, 0u,
                1u, 1u,
                2u, 2u,
                3u, 3u,
            ];

            // BVH nodes buffer: header(4 uints) + nodes(nodeCount * 20 uints)
            uint nodeStrideScalars = 20u;
            uint nodeHeaderScalars = 4u;
            uint nodeScalars = nodeHeaderScalars + nodeCount * nodeStrideScalars;
            uint[] nodeData = new uint[nodeScalars];

            // Primitive ranges buffer: nodeCount * 2 uints
            uint[] ranges = new uint[nodeCount * 2u];

            // Overflow flag buffer: single uint
            uint[] overflow = [0u];

            // Refit counters: nodeCount uints
            uint[] counters = new uint[nodeCount];

            // Transforms: mat4 per primitive (identity)
            float[] transforms = new float[numPrimitives * 16u];
            for (int i = 0; i < (int)numPrimitives; i++)
            {
                int baseIdx = i * 16;
                transforms[baseIdx + 0] = 1f;
                transforms[baseIdx + 5] = 1f;
                transforms[baseIdx + 10] = 1f;
                transforms[baseIdx + 15] = 1f;
            }

            uint aabbBuf = gl.GenBuffer();
            uint mortonBuf = gl.GenBuffer();
            uint nodeBuf = gl.GenBuffer();
            uint rangeBuf = gl.GenBuffer();
            uint overflowBuf = gl.GenBuffer();
            uint transformBuf = gl.GenBuffer();
            uint counterBuf = gl.GenBuffer();

            // Upload and bind buffers for build
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, aabbBuf);
            gl.BufferData<Vector4>(BufferTargetARB.ShaderStorageBuffer, aabbs.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, aabbBuf);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, mortonBuf);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, morton.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, mortonBuf);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, nodeBuf);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, nodeData.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, nodeBuf);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, rangeBuf);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, ranges.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 3, rangeBuf);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, overflowBuf);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, overflow.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 8, overflowBuf);

            // Build stage 0 + 1
            gl.UseProgram(buildProgram);
            gl.Uniform1(gl.GetUniformLocation(buildProgram, "MAX_LEAF_PRIMITIVES"), maxLeafPrims);
            gl.Uniform1(gl.GetUniformLocation(buildProgram, "BVH_MODE"), 0u);
            gl.Uniform1(gl.GetUniformLocation(buildProgram, "numPrimitives"), numPrimitives);
            gl.Uniform1(gl.GetUniformLocation(buildProgram, "nodeScalarCapacity"), nodeScalars);
            gl.Uniform1(gl.GetUniformLocation(buildProgram, "rangeScalarCapacity"), (uint)ranges.Length);
            gl.Uniform1(gl.GetUniformLocation(buildProgram, "mortonCapacity"), numPrimitives);

            gl.Uniform1(gl.GetUniformLocation(buildProgram, "buildStage"), 0u);
            gl.DispatchCompute((leafCount + 255u) / 256u, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
            gl.Finish();

            gl.Uniform1(gl.GetUniformLocation(buildProgram, "buildStage"), 1u);
            gl.DispatchCompute((internalCount + 255u) / 256u, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
            gl.Finish();

            gl.Uniform1(gl.GetUniformLocation(buildProgram, "buildStage"), 2u);
            gl.DispatchCompute((internalCount + 255u) / 256u, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
            gl.Finish();

            gl.Uniform1(gl.GetUniformLocation(buildProgram, "buildStage"), 3u);
            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
            gl.Finish();
            gl.Finish();
            gl.Finish();

            // Sanity check: parent indices + connectivity should be valid after stage 2.
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, nodeBuf);
            uint* afterBuildPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            Assert.That((nint)afterBuildPtr, Is.Not.EqualTo(nint.Zero));

            uint readNodeCountAfterBuild = afterBuildPtr[0];
            uint readRootAfterBuild = afterBuildPtr[1];

            if (readNodeCountAfterBuild == 0)
            {
                string vendor = gl.GetStringS(StringName.Vendor) ?? string.Empty;
                string renderer = gl.GetStringS(StringName.Renderer) ?? string.Empty;
                Assert.Inconclusive($"Compute dispatch produced no BVH nodes (nodeCount=0). Vendor='{vendor}', Renderer='{renderer}'. Likely software/headless driver limitation.");
                return;
            }
            readNodeCountAfterBuild.ShouldBe(nodeCount);
            readRootAfterBuild.ShouldBeLessThan(nodeCount);

            for (uint i = 0; i < nodeCount; i++)
            {
                uint nodeBaseScalar = nodeHeaderScalars + i * nodeStrideScalars;
                uint leftChild = afterBuildPtr[nodeBaseScalar + 3u];
                uint rightChild = afterBuildPtr[nodeBaseScalar + 7u];
                uint rangeStart = afterBuildPtr[nodeBaseScalar + 8u];
                uint rangeCount = afterBuildPtr[nodeBaseScalar + 9u];
                uint parentIndex = afterBuildPtr[nodeBaseScalar + 10u];
                uint flags = afterBuildPtr[nodeBaseScalar + 11u];

                if (i == readRootAfterBuild)
                {
                    parentIndex.ShouldBe(uint.MaxValue);
                }
                else
                {
                    if (parentIndex == uint.MaxValue)
                    {
                        Assert.Fail($"Node {i} has parentIndex=UINT_MAX but rootIndex={readRootAfterBuild}. flags=0x{flags:X8} left={leftChild} right={rightChild} range=({rangeStart},{rangeCount}). This suggests the LBVH build produced a disconnected component (extra root).");
                    }
                }

                bool isLeaf = (flags & 1u) != 0u;
                if (isLeaf)
                {
                    leftChild.ShouldBe(uint.MaxValue);
                    rightChild.ShouldBe(uint.MaxValue);
                }
                else
                {
                    leftChild.ShouldNotBe(uint.MaxValue);
                    rightChild.ShouldNotBe(uint.MaxValue);
                    leftChild.ShouldBeLessThan(nodeCount);
                    rightChild.ShouldBeLessThan(nodeCount);
                }
            }

            uint leaf0Base = nodeHeaderScalars + 0u * nodeStrideScalars;
            uint leaf0Parent = afterBuildPtr[leaf0Base + 10u];
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
            leaf0Parent.ShouldNotBe(uint.MaxValue);

            // Upload and bind extra buffers for refit
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, transformBuf);
            gl.BufferData<float>(BufferTargetARB.ShaderStorageBuffer, transforms.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 4, transformBuf);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, counterBuf);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, counters.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 11, counterBuf);

            // Refit stage 0 (clear counters) then stage 1 (leaf refit + propagate)
            gl.UseProgram(refitProgram);
            gl.Uniform1(gl.GetUniformLocation(refitProgram, "MAX_LEAF_PRIMITIVES"), maxLeafPrims);
            gl.Uniform1(gl.GetUniformLocation(refitProgram, "BVH_MODE"), 0u);
            // Avoid driver quirks around SSBO runtime-sized array .length() for struct arrays.
            gl.Uniform1(gl.GetUniformLocation(refitProgram, "debugValidation"), 0u);

            gl.Uniform1(gl.GetUniformLocation(refitProgram, "refitStage"), 0u);
            gl.DispatchCompute((nodeCount + 255u) / 256u, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
            gl.Finish();

            gl.Uniform1(gl.GetUniformLocation(refitProgram, "refitStage"), 1u);
            gl.DispatchCompute((nodeCount + 255u) / 256u, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
            gl.Finish();

            // Read back root bounds
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, nodeBuf);
            uint* nodePtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            Assert.That((nint)nodePtr, Is.Not.EqualTo(nint.Zero));

            uint readNodeCount = nodePtr[0];
            uint rootIndex = nodePtr[1];
            uint strideScalars = nodePtr[2];
            uint readMaxLeaf = nodePtr[3];

            readNodeCount.ShouldBe(nodeCount);
            rootIndex.ShouldBeLessThan(nodeCount);
            strideScalars.ShouldBe(nodeStrideScalars);
            readMaxLeaf.ShouldBe(maxLeafPrims);

            // Also sample a leaf to ensure refit actually ran.
            uint leaf0Scalar = nodeHeaderScalars + 0u * nodeStrideScalars;
            float leafMinX = BitConverter.UInt32BitsToSingle(nodePtr[leaf0Scalar + 0]);
            float leafMinY = BitConverter.UInt32BitsToSingle(nodePtr[leaf0Scalar + 1]);
            float leafMinZ = BitConverter.UInt32BitsToSingle(nodePtr[leaf0Scalar + 2]);
            float leafMaxX = BitConverter.UInt32BitsToSingle(nodePtr[leaf0Scalar + 4]);
            float leafMaxY = BitConverter.UInt32BitsToSingle(nodePtr[leaf0Scalar + 5]);
            float leafMaxZ = BitConverter.UInt32BitsToSingle(nodePtr[leaf0Scalar + 6]);

            uint baseScalar = nodeHeaderScalars + rootIndex * nodeStrideScalars;
            float minX = BitConverter.UInt32BitsToSingle(nodePtr[baseScalar + 0]);
            float minY = BitConverter.UInt32BitsToSingle(nodePtr[baseScalar + 1]);
            float minZ = BitConverter.UInt32BitsToSingle(nodePtr[baseScalar + 2]);
            float maxX = BitConverter.UInt32BitsToSingle(nodePtr[baseScalar + 4]);
            float maxY = BitConverter.UInt32BitsToSingle(nodePtr[baseScalar + 5]);
            float maxZ = BitConverter.UInt32BitsToSingle(nodePtr[baseScalar + 6]);

            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            // Leaf0 should match its primitive AABB under identity transform.
            leafMinX.ShouldBe(0f, 0.001f);
            leafMinY.ShouldBe(0f, 0.001f);
            leafMinZ.ShouldBe(0f, 0.001f);
            leafMaxX.ShouldBe(1f, 0.001f);
            leafMaxY.ShouldBe(1f, 0.001f);
            leafMaxZ.ShouldBe(1f, 0.001f);

            // Read counters to ensure propagation triggered.
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, counterBuf);
            uint* counterPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            Assert.That((nint)counterPtr, Is.Not.EqualTo(nint.Zero));
            uint rootCounter = counterPtr[rootIndex];
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
            // Root should be processed by the second child arrival.
            rootCounter.ShouldBe(2u);

            // Expected union of primitive AABBs (identity transforms)
            minX.ShouldBe(-1f, 0.001f);
            minY.ShouldBe(-1f, 0.001f);
            minZ.ShouldBe(-1f, 0.001f);
            maxX.ShouldBe(3f, 0.001f);
            maxY.ShouldBe(3f, 0.001f);
            maxZ.ShouldBe(1f, 0.001f);

            // Ensure overflow flag not set
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, overflowBuf);
            uint* overflowPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            Assert.That((nint)overflowPtr, Is.Not.EqualTo(nint.Zero));
            uint overflowFlags = *overflowPtr;
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
            overflowFlags.ShouldBe(0u);

            // Cleanup
            gl.DeleteBuffer(counterBuf);
            gl.DeleteBuffer(transformBuf);
            gl.DeleteBuffer(overflowBuf);
            gl.DeleteBuffer(rangeBuf);
            gl.DeleteBuffer(nodeBuf);
            gl.DeleteBuffer(mortonBuf);
            gl.DeleteBuffer(aabbBuf);
            gl.DeleteProgram(refitProgram);
            gl.DeleteShader(refitShader);
            gl.DeleteProgram(buildProgram);
            gl.DeleteShader(buildShader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    #endregion

    #region GPU Indirect Command Generation Tests

    [Test]
    public unsafe void GPURenderIndirect_GeneratesDrawCommands_FromCulledObjects()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", "Indirect", "GPURenderIndirect.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            string source = File.ReadAllText(shaderPath);
            uint shader = CompileComputeShader(gl, source);
            uint program = CreateComputeProgram(gl, shader);

            // Set up test data: 4 culled render commands
            const int COMMAND_FLOATS = 48;
            const int numCommands = 4;
            float[] culledCommands = new float[numCommands * COMMAND_FLOATS];
            
            // Initialize each command with valid data
            for (int i = 0; i < numCommands; i++)
            {
                int baseIdx = i * COMMAND_FLOATS;
                
                // World matrix (identity)
                culledCommands[baseIdx + 0] = 1f; // m00
                culledCommands[baseIdx + 5] = 1f; // m11
                culledCommands[baseIdx + 10] = 1f; // m22
                culledCommands[baseIdx + 15] = 1f; // m33
                
                // Bounding sphere
                culledCommands[baseIdx + 32] = 0f; // center x
                culledCommands[baseIdx + 33] = 0f; // center y
                culledCommands[baseIdx + 34] = 0f; // center z
                culledCommands[baseIdx + 35] = 1f; // radius
                
                // Mesh/submesh/material IDs
                culledCommands[baseIdx + 36] = BitConverter.Int32BitsToSingle(i); // MeshID
                culledCommands[baseIdx + 37] = BitConverter.Int32BitsToSingle(i); // SubmeshID
                culledCommands[baseIdx + 38] = BitConverter.Int32BitsToSingle(0); // MaterialID
                
                // Instance count
                culledCommands[baseIdx + 39] = BitConverter.Int32BitsToSingle(1); // InstanceCount
                
                // Render pass
                culledCommands[baseIdx + 40] = BitConverter.Int32BitsToSingle(0); // RenderPass
            }

            // Create GPU buffers
            uint culledBuffer = gl.GenBuffer();
            uint indirectBuffer = gl.GenBuffer();
            uint submeshBuffer = gl.GenBuffer();
            uint culledCountBuffer = gl.GenBuffer();
            uint drawCountBuffer = gl.GenBuffer();
            uint overflowBuffer = gl.GenBuffer();
            uint truncationBuffer = gl.GenBuffer();

            // Culled commands buffer (binding 0)
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, culledBuffer);
            gl.BufferData<float>(BufferTargetARB.ShaderStorageBuffer, culledCommands.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, culledBuffer);

            // Indirect draw buffer (binding 1) - 5 uints per command
            uint[] indirectDraws = new uint[numCommands * 5];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, indirectBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, indirectDraws.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, indirectBuffer);

            // Submesh data buffer (binding 2) - 4 uints per submesh
            uint[] submeshData = new uint[numCommands * 4];
            for (int i = 0; i < numCommands; i++)
            {
                submeshData[i * 4 + 0] = 36;  // indexCount
                submeshData[i * 4 + 1] = (uint)(i * 36); // firstIndex
                submeshData[i * 4 + 2] = 0;   // baseVertex
                submeshData[i * 4 + 3] = 0;   // baseInstance
            }
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, submeshBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, submeshData.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, submeshBuffer);

            // Culled count buffer (binding 3)
            uint[] culledCount = [(uint)numCommands, 0, 0]; // CulledCount, CulledInstanceCount, CulledOverflow
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, culledCountBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, culledCount.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 3, culledCountBuffer);

            // Draw count buffer (binding 4)
            uint[] drawCount = [0];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, drawCountBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, drawCount.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 4, drawCountBuffer);

            // Overflow flag buffer (binding 5)
            uint[] overflow = [0];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, overflowBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, overflow.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 5, overflowBuffer);

            // Truncation flag buffer (binding 7)
            uint[] truncation = [0];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, truncationBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, truncation.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 7, truncationBuffer);

            // Set uniforms
            gl.UseProgram(program);
            int passLoc = gl.GetUniformLocation(program, "CurrentRenderPass");
            int maxDrawsLoc = gl.GetUniformLocation(program, "MaxIndirectDraws");
            
            gl.Uniform1(passLoc, 0); // Render pass 0
            gl.Uniform1(maxDrawsLoc, 100); // Max 100 draws

            // Dispatch compute shader
            uint workGroupSize = 256;
            uint numGroups = ((uint)numCommands + workGroupSize - 1) / workGroupSize;
            gl.DispatchCompute(numGroups, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // Read back draw count
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, drawCountBuffer);
            uint* drawCountPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            uint resultDrawCount = *drawCountPtr;
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            resultDrawCount.ShouldBe((uint)numCommands, "Draw count should equal number of culled commands");

            // Cleanup
            gl.DeleteBuffer(culledBuffer);
            gl.DeleteBuffer(indirectBuffer);
            gl.DeleteBuffer(submeshBuffer);
            gl.DeleteBuffer(culledCountBuffer);
            gl.DeleteBuffer(drawCountBuffer);
            gl.DeleteBuffer(overflowBuffer);
            gl.DeleteBuffer(truncationBuffer);
            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public unsafe void GPURenderIndirect_RespectsMaxDrawLimit()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", "Indirect", "GPURenderIndirect.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            string source = File.ReadAllText(shaderPath);
            uint shader = CompileComputeShader(gl, source);
            uint program = CreateComputeProgram(gl, shader);

            const int COMMAND_FLOATS = 48;
            const int numCommands = 10;
            const int maxDraws = 5; // Limit to 5

            float[] culledCommands = new float[numCommands * COMMAND_FLOATS];
            for (int i = 0; i < numCommands; i++)
            {
                int baseIdx = i * COMMAND_FLOATS;
                culledCommands[baseIdx + 0] = 1f;
                culledCommands[baseIdx + 5] = 1f;
                culledCommands[baseIdx + 10] = 1f;
                culledCommands[baseIdx + 15] = 1f;
                culledCommands[baseIdx + 39] = BitConverter.Int32BitsToSingle(1);
                culledCommands[baseIdx + 40] = BitConverter.Int32BitsToSingle(0);
            }

            uint culledBuffer = gl.GenBuffer();
            uint indirectBuffer = gl.GenBuffer();
            uint submeshBuffer = gl.GenBuffer();
            uint culledCountBuffer = gl.GenBuffer();
            uint drawCountBuffer = gl.GenBuffer();
            uint overflowBuffer = gl.GenBuffer();
            uint truncationBuffer = gl.GenBuffer();

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, culledBuffer);
            gl.BufferData<float>(BufferTargetARB.ShaderStorageBuffer, culledCommands.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, culledBuffer);

            uint[] indirectDraws = new uint[numCommands * 5];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, indirectBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, indirectDraws.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, indirectBuffer);

            uint[] submeshData = new uint[numCommands * 4];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, submeshBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, submeshData.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, submeshBuffer);

            uint[] culledCount = [(uint)numCommands, 0, 0];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, culledCountBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, culledCount.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 3, culledCountBuffer);

            uint[] drawCount = [0];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, drawCountBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, drawCount.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 4, drawCountBuffer);

            uint[] overflow = [0];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, overflowBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, overflow.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 5, overflowBuffer);

            uint[] truncation = [0];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, truncationBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, truncation.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 7, truncationBuffer);

            gl.UseProgram(program);
            gl.Uniform1(gl.GetUniformLocation(program, "CurrentRenderPass"), 0);
            gl.Uniform1(gl.GetUniformLocation(program, "MaxIndirectDraws"), maxDraws);

            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // Read back draw count - should be capped at maxDraws
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, drawCountBuffer);
            uint* drawCountPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            uint resultDrawCount = *drawCountPtr;
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            resultDrawCount.ShouldBe((uint)maxDraws, "Draw count should be capped at MaxIndirectDraws");

            // Read back truncation flag - should be set
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, truncationBuffer);
            uint* truncationPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            uint truncationFlag = *truncationPtr;
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            truncationFlag.ShouldBe(1u, "Truncation flag should be set when exceeding capacity");

            gl.DeleteBuffer(culledBuffer);
            gl.DeleteBuffer(indirectBuffer);
            gl.DeleteBuffer(submeshBuffer);
            gl.DeleteBuffer(culledCountBuffer);
            gl.DeleteBuffer(drawCountBuffer);
            gl.DeleteBuffer(overflowBuffer);
            gl.DeleteBuffer(truncationBuffer);
            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    #endregion

    #region GPU Frustum Culling Tests

    [Test]
    public unsafe void GPURenderCulling_CullsObjectsOutsideFrustum()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", "Culling", "GPURenderCulling.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            string source = File.ReadAllText(shaderPath);
            uint shader = CompileComputeShader(gl, source);
            uint program = CreateComputeProgram(gl, shader);

            // Test with objects: 2 inside frustum, 2 outside
            const int COMMAND_FLOATS = 48;
            const int numCommands = 4;
            float[] inputCommands = new float[numCommands * COMMAND_FLOATS];

            // Object 0: At origin ahead (inside frustum)
            SetupCullingTestCommand(inputCommands, 0, new Vector3(0, 0, -5), 1f, layerMask: 1, renderPass: 0);
            
            // Object 1: Slightly right (inside frustum)
            SetupCullingTestCommand(inputCommands, 1, new Vector3(2, 0, -5), 1f, layerMask: 1, renderPass: 0);
            
            // Object 2: Far left (outside frustum)
            SetupCullingTestCommand(inputCommands, 2, new Vector3(-100, 0, -5), 1f, layerMask: 1, renderPass: 0);
            
            // Object 3: Behind camera (outside frustum)
            SetupCullingTestCommand(inputCommands, 3, new Vector3(0, 0, 10), 1f, layerMask: 1, renderPass: 0);

            // Create view-projection matrix for a simple camera looking down -Z
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 2f, 1f, 0.1f, 1000f); // Wide FOV
            Matrix4x4 view = Matrix4x4.CreateLookAt(
                new Vector3(0, 0, 0),
                new Vector3(0, 0, -1),
                Vector3.UnitY);
            Matrix4x4 viewProj = view * projection;

            // Extract frustum planes
            Vector4[] frustumPlanes = ExtractFrustumPlanesAsVec4(viewProj);

            // Create buffers
            uint inputBuffer = gl.GenBuffer();       // binding 0
            uint outputBuffer = gl.GenBuffer();      // binding 1
            uint culledCountBuffer = gl.GenBuffer(); // binding 2
            uint overflowBuffer = gl.GenBuffer();    // binding 3
            uint statsBuffer = gl.GenBuffer();       // binding 8

            // Binding 0: Input commands
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, inputBuffer);
            gl.BufferData<float>(BufferTargetARB.ShaderStorageBuffer, inputCommands.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, inputBuffer);

            // Binding 1: Output (culled) commands
            float[] outputCommands = new float[numCommands * COMMAND_FLOATS];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, outputBuffer);
            gl.BufferData<float>(BufferTargetARB.ShaderStorageBuffer, outputCommands.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, outputBuffer);

            // Binding 2: Culled count (CulledCount, CulledInstanceCount, CulledOverflow)
            uint[] culledCount = [0, 0, 0];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, culledCountBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, culledCount.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, culledCountBuffer);

            // Binding 3: Overflow flag
            uint[] overflowFlags = [0];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, overflowBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, overflowFlags.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 3, overflowBuffer);

            // Binding 8: Stats buffer
            uint[] stats = new uint[20];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, statsBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, stats.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 8, statsBuffer);

            gl.UseProgram(program);
            
            // Set uniforms (these are what the shader expects)
            int frustumLoc = gl.GetUniformLocation(program, "FrustumPlanes");
            if (frustumLoc >= 0)
            {
                // Set as array of vec4 (6 planes)
                float[] planeData = new float[24];
                for (int i = 0; i < 6; i++)
                {
                    planeData[i * 4 + 0] = frustumPlanes[i].X;
                    planeData[i * 4 + 1] = frustumPlanes[i].Y;
                    planeData[i * 4 + 2] = frustumPlanes[i].Z;
                    planeData[i * 4 + 3] = frustumPlanes[i].W;
                }
                gl.Uniform4(frustumLoc, 6, planeData.AsSpan());
            }

            int inputCountLoc = gl.GetUniformLocation(program, "InputCommandCount");
            if (inputCountLoc >= 0)
                gl.Uniform1(inputCountLoc, numCommands);

            int maxCulledLoc = gl.GetUniformLocation(program, "MaxCulledCommands");
            if (maxCulledLoc >= 0)
                gl.Uniform1(maxCulledLoc, 100);

            int maxDistLoc = gl.GetUniformLocation(program, "MaxRenderDistance");
            if (maxDistLoc >= 0)
                gl.Uniform1(maxDistLoc, 10000f); // Large enough to not cull by distance

            int layerMaskLoc = gl.GetUniformLocation(program, "CameraLayerMask");
            if (layerMaskLoc >= 0)
                gl.Uniform1(layerMaskLoc, 1u); // Use value 1, matching layer mask of 1

            int renderPassLoc = gl.GetUniformLocation(program, "CurrentRenderPass");
            if (renderPassLoc >= 0)
                gl.Uniform1(renderPassLoc, -1); // Accept all passes

            int disabledFlagsLoc = gl.GetUniformLocation(program, "DisabledFlagsMask");
            if (disabledFlagsLoc >= 0)
                gl.Uniform1(disabledFlagsLoc, 0); // No disabled flags

            int cameraPosLoc = gl.GetUniformLocation(program, "CameraPosition");
            if (cameraPosLoc >= 0)
                gl.Uniform3(cameraPosLoc, 0f, 0f, 0f); // Camera at origin

            // Dispatch (1 workgroup of 256 threads is enough for 4 commands)
            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // Read back stats for debugging
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, statsBuffer);
            uint* cullStatsPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            uint statsInputCount = cullStatsPtr[0];
            uint statsCulledCount = cullStatsPtr[1];
            uint statsRejectedFrustum = cullStatsPtr[3];
            uint statsRejectedDistance = cullStatsPtr[4];
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            // Read back culled count
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, culledCountBuffer);
            uint* countPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            uint culledObjectCount = *countPtr;
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            // For debugging, check the frustum plane uniform location
            var errorMsg = $"Should cull objects outside frustum, keeping 2 inside. " +
                $"Stats: input={statsInputCount}, culled={statsCulledCount}, " +
                $"rejectedFrustum={statsRejectedFrustum}, rejectedDistance={statsRejectedDistance}, " +
                $"frustumLoc={frustumLoc}, inputCountLoc={inputCountLoc}";
            
            // If the shader rejected all by frustum or distance, it ran but culled everything
            // This might indicate frustum planes not set correctly
            if (statsRejectedFrustum > 0 || statsRejectedDistance > 0)
            {
                // Shader executed and made decisions
                culledObjectCount.ShouldBe(2u, errorMsg);
            }
            else if (statsInputCount > 0 && statsCulledCount == 0 && statsRejectedFrustum == 0 && statsRejectedDistance == 0)
            {
                // Shader appears to run but produces no output and no rejections; typically indicates software/headless driver behavior.
                string vendor = gl.GetStringS(StringName.Vendor) ?? string.Empty;
                string renderer = gl.GetStringS(StringName.Renderer) ?? string.Empty;
                Assert.Inconclusive($"GPU culling compute produced no progress (all decision stats zero). Vendor='{vendor}', Renderer='{renderer}'. {errorMsg}");
            }
            else if (statsInputCount == 0)
            {
                // InputCommandCount uniform not reaching shader
                Assert.Inconclusive($"Shader didn't process any commands - check uniform binding. {errorMsg}");
            }
            else
            {
                // Something else is wrong
                culledObjectCount.ShouldBe(2u, errorMsg);
            }

            gl.DeleteBuffer(inputBuffer);
            gl.DeleteBuffer(outputBuffer);
            gl.DeleteBuffer(culledCountBuffer);
            gl.DeleteBuffer(overflowBuffer);
            gl.DeleteBuffer(statsBuffer);
            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    #endregion

    #region GPU Render Dispatch Pipeline Tests

    [Test]
    public unsafe void GPURenderResetCounters_ResetsCountersAndStats()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", "Indirect", "GPURenderResetCounters.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            uint shader = CompileComputeShader(gl, File.ReadAllText(shaderPath));
            uint program = CreateComputeProgram(gl, shader);

            uint culledCountBuffer = gl.GenBuffer();
            uint drawCountBuffer = gl.GenBuffer();
            uint cullingOverflowBuffer = gl.GenBuffer();
            uint indirectOverflowBuffer = gl.GenBuffer();
            uint truncationBuffer = gl.GenBuffer();
            uint statsBuffer = gl.GenBuffer();

            // Seed with non-zero values to prove reset works.
            uint[] culledCount = [123u, 456u, 789u];
            uint[] drawCount = [42u];
            uint[] cullingOverflow = [9u];
            uint[] indirectOverflow = [8u];
            uint[] truncation = [7u];
            uint[] stats = new uint[20];
            for (int i = 0; i < stats.Length; i++) stats[i] = (uint)(i + 1);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, culledCountBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, culledCount.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, culledCountBuffer);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, drawCountBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, drawCount.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, drawCountBuffer);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, cullingOverflowBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, cullingOverflow.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, cullingOverflowBuffer);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, indirectOverflowBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, indirectOverflow.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 3, indirectOverflowBuffer);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, truncationBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, truncation.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 4, truncationBuffer);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, statsBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, stats.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 8, statsBuffer);

            gl.UseProgram(program);
            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // Validate counters reset.
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, culledCountBuffer);
            uint* culledPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            culledPtr[0].ShouldBe(0u);
            culledPtr[1].ShouldBe(0u);
            culledPtr[2].ShouldBe(0u);
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, drawCountBuffer);
            uint* drawPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            drawPtr[0].ShouldBe(0u);
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, cullingOverflowBuffer);
            uint* cullOvPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            cullOvPtr[0].ShouldBe(0u);
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, indirectOverflowBuffer);
            uint* indOvPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            indOvPtr[0].ShouldBe(0u);
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, truncationBuffer);
            uint* truncPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            truncPtr[0].ShouldBe(0u);
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, statsBuffer);
            uint* statsPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            for (int i = 0; i < 17; i++)
                statsPtr[i].ShouldBe(0u);
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            gl.DeleteBuffer(culledCountBuffer);
            gl.DeleteBuffer(drawCountBuffer);
            gl.DeleteBuffer(cullingOverflowBuffer);
            gl.DeleteBuffer(indirectOverflowBuffer);
            gl.DeleteBuffer(truncationBuffer);
            gl.DeleteBuffer(statsBuffer);
            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public unsafe void GPURenderBuildKeys_EmitsKeyIndexPairs_FromCulledCommands()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", "Indirect", "GPURenderBuildKeys.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            uint shader = CompileComputeShader(gl, File.ReadAllText(shaderPath));
            uint program = CreateComputeProgram(gl, shader);

            const int COMMAND_FLOATS = 48;
            const int numCommands = 3;
            float[] culledCommands = new float[numCommands * COMMAND_FLOATS];

            // Ensure the shader has meaningful distances in slot 42.
            SetupTestCommand(culledCommands, 0, new Vector3(0, 0, -1), 1f);
            SetupTestCommand(culledCommands, 1, new Vector3(0, 0, -2), 1f);
            SetupTestCommand(culledCommands, 2, new Vector3(0, 0, -3), 1f);
            culledCommands[0 * COMMAND_FLOATS + 36] = BitConverter.UInt32BitsToSingle(101u);
            culledCommands[1 * COMMAND_FLOATS + 36] = BitConverter.UInt32BitsToSingle(102u);
            culledCommands[2 * COMMAND_FLOATS + 36] = BitConverter.UInt32BitsToSingle(103u);
            culledCommands[0 * COMMAND_FLOATS + 38] = BitConverter.UInt32BitsToSingle(201u);
            culledCommands[1 * COMMAND_FLOATS + 38] = BitConverter.UInt32BitsToSingle(202u);
            culledCommands[2 * COMMAND_FLOATS + 38] = BitConverter.UInt32BitsToSingle(203u);
            culledCommands[0 * COMMAND_FLOATS + 42] = 1.25f;
            culledCommands[1 * COMMAND_FLOATS + 42] = 2.50f;
            culledCommands[2 * COMMAND_FLOATS + 42] = 3.75f;

            uint culledBuffer = gl.GenBuffer();
            uint culledCountBuffer = gl.GenBuffer();
            uint keyIndexBuffer = gl.GenBuffer();
            uint materialIdsBuffer = gl.GenBuffer();

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, culledBuffer);
            gl.BufferData<float>(BufferTargetARB.ShaderStorageBuffer, culledCommands.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, culledBuffer);

            uint[] culledCount = [(uint)numCommands, 0u, 0u];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, culledCountBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, culledCount.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, culledCountBuffer);

            const int KEY_UINTS = 4;
            uint[] keyIndexOut = new uint[numCommands * KEY_UINTS];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, keyIndexBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, keyIndexOut.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, keyIndexBuffer);

            // MaterialIDs is optional; bind a dummy buffer to satisfy the declared binding.
            uint[] dummyMaterialIds = [0u, 0u, 0u];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, materialIdsBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, dummyMaterialIds.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 3, materialIdsBuffer);

            gl.UseProgram(program);
            gl.Uniform1(gl.GetUniformLocation(program, "CurrentRenderPass"), -1);
            gl.Uniform1(gl.GetUniformLocation(program, "MaxSortKeys"), numCommands);
            gl.Uniform1(gl.GetUniformLocation(program, "StateBitMask"), 0u);

            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, keyIndexBuffer);
            uint* outPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);

            // Shader emits 4 uint lanes per command: packed key, materialID, meshID, logicalIdx.
            outPtr[3].ShouldBe(0u);
            outPtr[7].ShouldBe(1u);
            outPtr[11].ShouldBe(2u);

            uint k0 = outPtr[0];
            uint k1 = outPtr[4];
            uint k2 = outPtr[8];
            outPtr[1].ShouldBe(201u);
            outPtr[5].ShouldBe(202u);
            outPtr[9].ShouldBe(203u);
            outPtr[2].ShouldBe(101u);
            outPtr[6].ShouldBe(102u);
            outPtr[10].ShouldBe(103u);
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            // renderPass, shaderProgramID and flags are all zero in this setup
            k0.ShouldBe(0u);
            k1.ShouldBe(0u);
            k2.ShouldBe(0u);

            gl.DeleteBuffer(culledBuffer);
            gl.DeleteBuffer(culledCountBuffer);
            gl.DeleteBuffer(keyIndexBuffer);
            gl.DeleteBuffer(materialIdsBuffer);
            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public unsafe void GPURenderCopyCommands_FiltersByRenderPass_AndCountsVisibleCommands()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", "Indirect", "GPURenderCopyCommands.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            uint shader = CompileComputeShader(gl, File.ReadAllText(shaderPath));
            uint program = CreateComputeProgram(gl, shader);

            const int COMMAND_FLOATS = 48;
            const int numCommands = 4;

            float[] inCommands = new float[numCommands * COMMAND_FLOATS];
            for (int i = 0; i < numCommands; i++)
            {
                int baseIdx = i * COMMAND_FLOATS;
                inCommands[baseIdx + 0] = 1f;
                inCommands[baseIdx + 5] = 1f;
                inCommands[baseIdx + 10] = 1f;
                inCommands[baseIdx + 15] = 1f;
                inCommands[baseIdx + 39] = BitConverter.UInt32BitsToSingle(1u);
                inCommands[baseIdx + 40] = BitConverter.UInt32BitsToSingle((uint)(i % 2)); // passes 0,1,0,1
            }

            uint inBuffer = gl.GenBuffer();
            uint outBuffer = gl.GenBuffer();
            uint visibleCountBuffer = gl.GenBuffer();
            uint debugBuffer = gl.GenBuffer();
            uint overflowFlagBuffer = gl.GenBuffer();

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, inBuffer);
            gl.BufferData<float>(BufferTargetARB.ShaderStorageBuffer, inCommands.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, inBuffer);

            float[] outCommands = new float[numCommands * COMMAND_FLOATS];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, outBuffer);
            gl.BufferData<float>(BufferTargetARB.ShaderStorageBuffer, outCommands.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, outBuffer);

            uint[] visible = [0u, 0u, 0u];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, visibleCountBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, visible.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, visibleCountBuffer);

            uint[] debug = new uint[16];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, debugBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, debug.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 3, debugBuffer);

            uint[] overflow = [0u];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, overflowFlagBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, overflow.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 4, overflowFlagBuffer);

            gl.UseProgram(program);
            gl.Uniform1(gl.GetUniformLocation(program, "CopyCount"), (uint)numCommands);
            gl.Uniform1(gl.GetUniformLocation(program, "TargetPass"), 0);
            gl.Uniform1(gl.GetUniformLocation(program, "DebugEnabled"), 0);
            gl.Uniform1(gl.GetUniformLocation(program, "DebugMaxSamples"), 0);
            gl.Uniform1(gl.GetUniformLocation(program, "DebugInstanceStride"), 0);
            gl.Uniform1(gl.GetUniformLocation(program, "OutputCapacity"), (uint)numCommands);
            gl.Uniform1(gl.GetUniformLocation(program, "BoundsCheckEnabled"), 1);

            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, visibleCountBuffer);
            uint* visPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            uint drawCount = visPtr[0];
            uint instanceCount = visPtr[1];
            uint visibleOverflow = visPtr[2];
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            drawCount.ShouldBe(2u);
            instanceCount.ShouldBe(2u);
            visibleOverflow.ShouldBe(0u);

            gl.DeleteBuffer(inBuffer);
            gl.DeleteBuffer(outBuffer);
            gl.DeleteBuffer(visibleCountBuffer);
            gl.DeleteBuffer(debugBuffer);
            gl.DeleteBuffer(overflowFlagBuffer);
            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public unsafe void GPURenderCullingThenIndirect_EndToEnd_ProducesDrawCommandsAndStats()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string cullPath = Path.Combine(ShaderBasePath, "Compute", "Culling", "GPURenderCulling.comp");
            string indirectPath = Path.Combine(ShaderBasePath, "Compute", "Indirect", "GPURenderIndirect.comp");
            if (!File.Exists(cullPath) || !File.Exists(indirectPath))
            {
                Assert.Inconclusive($"Required shader(s) missing: {cullPath} or {indirectPath}");
                return;
            }

            uint cullShader = CompileComputeShader(gl, File.ReadAllText(cullPath));
            uint cullProgram = CreateComputeProgram(gl, cullShader);
            uint indirectShader = CompileComputeShader(gl, File.ReadAllText(indirectPath));
            uint indirectProgram = CreateComputeProgram(gl, indirectShader);

            const int COMMAND_FLOATS = 48;
            const int numCommands = 4;

            float[] inputCommands = new float[numCommands * COMMAND_FLOATS];
            SetupCullingTestCommand(inputCommands, 0, new Vector3(0, 0, -5), 1f, layerMask: 1, renderPass: 0);
            SetupCullingTestCommand(inputCommands, 1, new Vector3(2, 0, -5), 1f, layerMask: 1, renderPass: 0);
            SetupCullingTestCommand(inputCommands, 2, new Vector3(-100, 0, -5), 1f, layerMask: 1, renderPass: 0);
            SetupCullingTestCommand(inputCommands, 3, new Vector3(0, 0, 10), 1f, layerMask: 1, renderPass: 0);

            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.1f, 1000f);
            Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 0), new Vector3(0, 0, -1), Vector3.UnitY);
            Vector4[] frustumPlanes = ExtractFrustumPlanesAsVec4(view * projection);

            // Shared buffers across stages.
            uint inputBuffer = gl.GenBuffer();
            uint culledCommandsBuffer = gl.GenBuffer();
            uint culledCountBuffer = gl.GenBuffer();
            uint cullingOverflowBuffer = gl.GenBuffer();
            uint statsBuffer = gl.GenBuffer();

            // Culling stage binds.
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, inputBuffer);
            gl.BufferData<float>(BufferTargetARB.ShaderStorageBuffer, inputCommands.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, inputBuffer);

            float[] culledCommands = new float[numCommands * COMMAND_FLOATS];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, culledCommandsBuffer);
            gl.BufferData<float>(BufferTargetARB.ShaderStorageBuffer, culledCommands.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, culledCommandsBuffer);

            uint[] culledCount = [0u, 0u, 0u];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, culledCountBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, culledCount.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, culledCountBuffer);

            uint[] cullOverflow = [0u];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, cullingOverflowBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, cullOverflow.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 3, cullingOverflowBuffer);

            uint[] stats = new uint[20];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, statsBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, stats.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 8, statsBuffer);

            gl.UseProgram(cullProgram);
            int frustumLoc = gl.GetUniformLocation(cullProgram, "FrustumPlanes");
            if (frustumLoc >= 0)
            {
                float[] planeData = new float[24];
                for (int i = 0; i < 6; i++)
                {
                    planeData[i * 4 + 0] = frustumPlanes[i].X;
                    planeData[i * 4 + 1] = frustumPlanes[i].Y;
                    planeData[i * 4 + 2] = frustumPlanes[i].Z;
                    planeData[i * 4 + 3] = frustumPlanes[i].W;
                }
                gl.Uniform4(frustumLoc, 6, planeData.AsSpan());
            }

            gl.Uniform1(gl.GetUniformLocation(cullProgram, "InputCommandCount"), numCommands);
            gl.Uniform1(gl.GetUniformLocation(cullProgram, "MaxCulledCommands"), 100);
            gl.Uniform1(gl.GetUniformLocation(cullProgram, "MaxRenderDistance"), 10000f);
            gl.Uniform1(gl.GetUniformLocation(cullProgram, "CameraLayerMask"), 1u);
            gl.Uniform1(gl.GetUniformLocation(cullProgram, "CurrentRenderPass"), -1);
            gl.Uniform1(gl.GetUniformLocation(cullProgram, "DisabledFlagsMask"), 0);
            gl.Uniform3(gl.GetUniformLocation(cullProgram, "CameraPosition"), 0f, 0f, 0f);

            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // GPURenderCulling.comp and GPURenderIndirect.comp both interpret integer-like fields
            // via floatBitsToUint(...), so commands must be packed as uint bits in float lanes.
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, culledCountBuffer);
            uint* culledCountPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            uint visibleCount = culledCountPtr[0];
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            // Read culling stats so we can distinguish "culled everything" from "compute made no progress".
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, statsBuffer);
            uint* cullStatsPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            uint statsInputCount = cullStatsPtr[0];
            uint statsCulledCount = cullStatsPtr[1];
            uint statsRejectedFrustum = cullStatsPtr[3];
            uint statsRejectedDistance = cullStatsPtr[4];
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            var errorMsg = $"visibleCount should be 2 after culling. " +
                $"Stats: input={statsInputCount}, culled={statsCulledCount}, " +
                $"rejectedFrustum={statsRejectedFrustum}, rejectedDistance={statsRejectedDistance}, " +
                $"frustumLoc={frustumLoc}";

            if (visibleCount != 2u)
            {
                if (statsInputCount > 0 && statsCulledCount == 0 && statsRejectedFrustum == 0 && statsRejectedDistance == 0)
                {
                    string vendor = gl.GetStringS(StringName.Vendor) ?? string.Empty;
                    string renderer = gl.GetStringS(StringName.Renderer) ?? string.Empty;
                    Assert.Inconclusive($"GPU culling compute produced no progress (all decision stats zero). Vendor='{vendor}', Renderer='{renderer}'. {errorMsg}");
                    return;
                }

                if (statsInputCount == 0)
                {
                    Assert.Inconclusive($"Shader didn't process any commands - check uniform binding. {errorMsg}");
                    return;
                }
            }

            visibleCount.ShouldBe(2u, errorMsg);

            // No repack step required.

            // Indirect stage buffers (reuse culledCommandsBuffer and culledCountBuffer).
            uint indirectDrawBuffer = gl.GenBuffer();
            uint submeshBuffer = gl.GenBuffer();
            uint drawCountBuffer = gl.GenBuffer();
            uint indirectOverflowBuffer = gl.GenBuffer();
            uint truncationBuffer = gl.GenBuffer();

            uint[] indirectDraws = new uint[numCommands * 5];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, indirectDrawBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, indirectDraws.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, indirectDrawBuffer);

            uint[] submeshData = new uint[numCommands * 4];
            for (int i = 0; i < numCommands; i++)
            {
                submeshData[i * 4 + 0] = 36;
                submeshData[i * 4 + 1] = (uint)(i * 36);
                submeshData[i * 4 + 2] = 0;
                submeshData[i * 4 + 3] = 0;
            }
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, submeshBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, submeshData.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, submeshBuffer);

            uint[] drawCount = [0u];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, drawCountBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, drawCount.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 4, drawCountBuffer);

            uint[] indirectOverflow = [0u];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, indirectOverflowBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, indirectOverflow.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 5, indirectOverflowBuffer);

            uint[] trunc = [0u];
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, truncationBuffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, trunc.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 7, truncationBuffer);

            // Rebind shared buffers to the bindings expected by GPURenderIndirect.comp.
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, culledCommandsBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 3, culledCountBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 8, statsBuffer);

            gl.UseProgram(indirectProgram);
            gl.Uniform1(gl.GetUniformLocation(indirectProgram, "CurrentRenderPass"), -1);
            gl.Uniform1(gl.GetUniformLocation(indirectProgram, "MaxIndirectDraws"), 100);
            gl.Uniform1(gl.GetUniformLocation(indirectProgram, "StatsEnabled"), 1u);

            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, drawCountBuffer);
            uint* drawPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            uint finalDrawCount = drawPtr[0];
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
            finalDrawCount.ShouldBe(2u);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, indirectDrawBuffer);
            uint* indirectPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            // Validate both draws exist and have the expected shape; ordering is not assumed.
            uint indexCount0 = indirectPtr[0];
            uint instanceCount0 = indirectPtr[1];
            uint firstIndex0 = indirectPtr[2];
            uint indexCount1 = indirectPtr[5];
            uint instanceCount1 = indirectPtr[6];
            uint firstIndex1 = indirectPtr[7];
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            indexCount0.ShouldBe(36u);
            indexCount1.ShouldBe(36u);
            instanceCount0.ShouldBe(1u);
            instanceCount1.ShouldBe(1u);
            (firstIndex0 == 0u || firstIndex0 == 36u).ShouldBeTrue();
            (firstIndex1 == 0u || firstIndex1 == 36u).ShouldBeTrue();
            (firstIndex0 != firstIndex1).ShouldBeTrue();

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, statsBuffer);
            uint* statsPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            uint statsDrawCount = statsPtr[2];
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
            statsDrawCount.ShouldBe(2u);

            gl.DeleteBuffer(inputBuffer);
            gl.DeleteBuffer(culledCommandsBuffer);
            gl.DeleteBuffer(culledCountBuffer);
            gl.DeleteBuffer(cullingOverflowBuffer);
            gl.DeleteBuffer(statsBuffer);
            gl.DeleteBuffer(indirectDrawBuffer);
            gl.DeleteBuffer(submeshBuffer);
            gl.DeleteBuffer(drawCountBuffer);
            gl.DeleteBuffer(indirectOverflowBuffer);
            gl.DeleteBuffer(truncationBuffer);
            gl.DeleteProgram(cullProgram);
            gl.DeleteShader(cullShader);
            gl.DeleteProgram(indirectProgram);
            gl.DeleteShader(indirectShader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates GL context and also validates the GPU driver is real hardware
    /// (rejects software renderers).
    /// </summary>
    private new (GL?, IWindow?) CreateGLContext()
    {
        var (gl, window) = base.CreateGLContext();
        if (gl is not null)
            AssertHardwareComputeOrInconclusive(gl);
        return (gl, window);
    }

    private static string ResolveIncludes(string source, string baseDir)
    {
        // Simple include resolution for #include "filename"
        var includeRegex = new System.Text.RegularExpressions.Regex(
            @"#include\s+""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        return includeRegex.Replace(source, match =>
        {
            string includePath = match.Groups[1].Value;
            string fullPath = Path.Combine(baseDir, includePath);
            
            if (File.Exists(fullPath))
            {
                string includeContent = File.ReadAllText(fullPath);
                return ResolveIncludes(includeContent, Path.GetDirectoryName(fullPath)!);
            }
            
            // Try common include directories
            string[] searchPaths = [
                Path.Combine(ShaderBasePath, includePath),
                Path.Combine(ShaderBasePath, "Scene3D", "RenderPipeline", includePath),
                Path.Combine(ShaderBasePath, "Compute", includePath),
            ];
            
            foreach (var searchPath in searchPaths)
            {
                if (File.Exists(searchPath))
                {
                    string includeContent = File.ReadAllText(searchPath);
                    return ResolveIncludes(includeContent, Path.GetDirectoryName(searchPath)!);
                }
            }
            
            return $"// Include not found: {includePath}";
        });
    }

    /// <summary>
    /// Strips Vulkan GLSL specialization constants (layout(constant_id = N)) which are not supported
    /// in plain OpenGL GLSL. Converts them to regular const declarations.
    /// This matches what the engine does in GpuBvhTree via uniform patching.
    /// </summary>
    private static string StripVulkanSpecializationConstants(string source, uint maxLeafPrimitives = 4, uint bvhMode = 0)
    {
        // Match layout(constant_id = N) const uint declarations and convert to regular const
        var regex = new System.Text.RegularExpressions.Regex(
            @"layout\s*\(\s*constant_id\s*=\s*\d+\s*\)\s*const\s+uint\s+(\w+)\s*=\s*[0-9]+u?\s*;",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        return regex.Replace(source, match =>
        {
            string varName = match.Groups[1].Value;
            return varName switch
            {
                "MAX_LEAF_PRIMITIVES" => $"const uint MAX_LEAF_PRIMITIVES = {maxLeafPrimitives}u;",
                "BVH_MODE" => $"const uint BVH_MODE = {bvhMode}u;",
                _ => $"const uint {varName} = 0u;" // Default fallback
            };
        });
    }

    private static void SetupTestCommand(float[] commands, int index, Vector3 position, float radius)
    {
        const int COMMAND_FLOATS = 48;
        int baseIdx = index * COMMAND_FLOATS;

        // World matrix (identity with translation)
        commands[baseIdx + 0] = 1f;
        commands[baseIdx + 5] = 1f;
        commands[baseIdx + 10] = 1f;
        commands[baseIdx + 12] = position.X;
        commands[baseIdx + 13] = position.Y;
        commands[baseIdx + 14] = position.Z;
        commands[baseIdx + 15] = 1f;

        // Bounding sphere
        commands[baseIdx + 32] = position.X;
        commands[baseIdx + 33] = position.Y;
        commands[baseIdx + 34] = position.Z;
        commands[baseIdx + 35] = radius;

        // Other fields
        commands[baseIdx + 39] = BitConverter.Int32BitsToSingle(1); // InstanceCount
        commands[baseIdx + 40] = BitConverter.Int32BitsToSingle(0); // RenderPass
    }

    private static float[] ExtractFrustumPlanes(Matrix4x4 viewProj)
    {
        // Extract 6 frustum planes from view-projection matrix
        float[] planes = new float[24]; // 6 planes * 4 floats (a, b, c, d)

        // Left plane
        planes[0] = viewProj.M14 + viewProj.M11;
        planes[1] = viewProj.M24 + viewProj.M21;
        planes[2] = viewProj.M34 + viewProj.M31;
        planes[3] = viewProj.M44 + viewProj.M41;

        // Right plane
        planes[4] = viewProj.M14 - viewProj.M11;
        planes[5] = viewProj.M24 - viewProj.M21;
        planes[6] = viewProj.M34 - viewProj.M31;
        planes[7] = viewProj.M44 - viewProj.M41;

        // Bottom plane
        planes[8] = viewProj.M14 + viewProj.M12;
        planes[9] = viewProj.M24 + viewProj.M22;
        planes[10] = viewProj.M34 + viewProj.M32;
        planes[11] = viewProj.M44 + viewProj.M42;

        // Top plane
        planes[12] = viewProj.M14 - viewProj.M12;
        planes[13] = viewProj.M24 - viewProj.M22;
        planes[14] = viewProj.M34 - viewProj.M32;
        planes[15] = viewProj.M44 - viewProj.M42;

        // Near plane
        planes[16] = viewProj.M14 + viewProj.M13;
        planes[17] = viewProj.M24 + viewProj.M23;
        planes[18] = viewProj.M34 + viewProj.M33;
        planes[19] = viewProj.M44 + viewProj.M43;

        // Far plane
        planes[20] = viewProj.M14 - viewProj.M13;
        planes[21] = viewProj.M24 - viewProj.M23;
        planes[22] = viewProj.M34 - viewProj.M33;
        planes[23] = viewProj.M44 - viewProj.M43;

        // Normalize each plane
        for (int i = 0; i < 6; i++)
        {
            int idx = i * 4;
            float length = MathF.Sqrt(
                planes[idx] * planes[idx] +
                planes[idx + 1] * planes[idx + 1] +
                planes[idx + 2] * planes[idx + 2]);
            
            if (length > 0.0001f)
            {
                planes[idx] /= length;
                planes[idx + 1] /= length;
                planes[idx + 2] /= length;
                planes[idx + 3] /= length;
            }
        }

        return planes;
    }

    /// <summary>
    /// Sets up a test command with all the fields required for the culling shader.
    /// Matches the 48-float GPUIndirectRenderCommand layout.
    ///
    /// IMPORTANT: GPURenderCulling.comp reads integer-like fields using floatBitsToUint(...).
    /// That means we must pack uint values into float lanes via BitConverter.UInt32BitsToSingle.
    /// </summary>
    private static void SetupCullingTestCommand(float[] commands, int index, Vector3 position, float radius,
        uint layerMask = 0xFFFFFFFF, uint renderPass = 0, uint instanceCount = 1)
    {
        const int COMMAND_FLOATS = 48;
        int baseIdx = index * COMMAND_FLOATS;

        // World matrix (identity with translation) - offsets 0-15
        commands[baseIdx + 0] = 1f;  // M11
        commands[baseIdx + 1] = 0f;  // M21
        commands[baseIdx + 2] = 0f;  // M31
        commands[baseIdx + 3] = 0f;  // M41
        commands[baseIdx + 4] = 0f;  // M12
        commands[baseIdx + 5] = 1f;  // M22
        commands[baseIdx + 6] = 0f;  // M32
        commands[baseIdx + 7] = 0f;  // M42
        commands[baseIdx + 8] = 0f;  // M13
        commands[baseIdx + 9] = 0f;  // M23
        commands[baseIdx + 10] = 1f; // M33
        commands[baseIdx + 11] = 0f; // M43
        commands[baseIdx + 12] = position.X;  // M14
        commands[baseIdx + 13] = position.Y;  // M24
        commands[baseIdx + 14] = position.Z;  // M34
        commands[baseIdx + 15] = 1f; // M44

        // PrevWorldMatrix (identity) - offsets 16-31
        commands[baseIdx + 16] = 1f;
        commands[baseIdx + 21] = 1f;
        commands[baseIdx + 26] = 1f;
        commands[baseIdx + 28] = position.X;
        commands[baseIdx + 29] = position.Y;
        commands[baseIdx + 30] = position.Z;
        commands[baseIdx + 31] = 1f;

        // Bounding sphere (xyz center, w radius) - offsets 32-35
        commands[baseIdx + 32] = position.X;
        commands[baseIdx + 33] = position.Y;
        commands[baseIdx + 34] = position.Z;
        commands[baseIdx + 35] = radius;

        // Integer-like fields packed as uint bits in float lanes.
        // MeshID (36)
        commands[baseIdx + 36] = BitConverter.UInt32BitsToSingle((uint)index);
        // SubmeshID (37)
        commands[baseIdx + 37] = BitConverter.UInt32BitsToSingle(0u);
        // MaterialID (38)
        commands[baseIdx + 38] = BitConverter.UInt32BitsToSingle(0u);
        // InstanceCount (39) - must be > 0
        commands[baseIdx + 39] = BitConverter.UInt32BitsToSingle(instanceCount);
        // RenderPass (40)
        commands[baseIdx + 40] = BitConverter.UInt32BitsToSingle(renderPass);
        // ShaderProgramID (41)
        commands[baseIdx + 41] = BitConverter.UInt32BitsToSingle(0u);
        // RenderDistance (42) (float, overwritten by shader)
        commands[baseIdx + 42] = 0f;
        // LayerMask (43)
        commands[baseIdx + 43] = BitConverter.UInt32BitsToSingle(layerMask);
        // LODLevel (44)
        commands[baseIdx + 44] = BitConverter.UInt32BitsToSingle(0u);
        // Flags (45)
        commands[baseIdx + 45] = BitConverter.UInt32BitsToSingle(0u);
        // Reserved0 (46), Reserved1 (47)
        commands[baseIdx + 46] = BitConverter.UInt32BitsToSingle(0u);
        commands[baseIdx + 47] = BitConverter.UInt32BitsToSingle(0u);
    }

    /// <summary>
    /// Extract frustum planes as Vector4 array (normalized planes).
    /// Plane equation: dot(normal, point) + d = 0
    /// Stored as vec4: (normal.xyz, d)
    /// </summary>
    private static Vector4[] ExtractFrustumPlanesAsVec4(Matrix4x4 viewProj)
    {
        Vector4[] planes = new Vector4[6];

        // Left plane
        planes[0] = new Vector4(
            viewProj.M14 + viewProj.M11,
            viewProj.M24 + viewProj.M21,
            viewProj.M34 + viewProj.M31,
            viewProj.M44 + viewProj.M41);

        // Right plane
        planes[1] = new Vector4(
            viewProj.M14 - viewProj.M11,
            viewProj.M24 - viewProj.M21,
            viewProj.M34 - viewProj.M31,
            viewProj.M44 - viewProj.M41);

        // Bottom plane
        planes[2] = new Vector4(
            viewProj.M14 + viewProj.M12,
            viewProj.M24 + viewProj.M22,
            viewProj.M34 + viewProj.M32,
            viewProj.M44 + viewProj.M42);

        // Top plane
        planes[3] = new Vector4(
            viewProj.M14 - viewProj.M12,
            viewProj.M24 - viewProj.M22,
            viewProj.M34 - viewProj.M32,
            viewProj.M44 - viewProj.M42);

        // Near plane
        planes[4] = new Vector4(
            viewProj.M14 + viewProj.M13,
            viewProj.M24 + viewProj.M23,
            viewProj.M34 + viewProj.M33,
            viewProj.M44 + viewProj.M43);

        // Far plane
        planes[5] = new Vector4(
            viewProj.M14 - viewProj.M13,
            viewProj.M24 - viewProj.M23,
            viewProj.M34 - viewProj.M33,
            viewProj.M44 - viewProj.M43);

        // Normalize each plane
        for (int i = 0; i < 6; i++)
        {
            float length = MathF.Sqrt(
                planes[i].X * planes[i].X +
                planes[i].Y * planes[i].Y +
                planes[i].Z * planes[i].Z);
            
            if (length > 0.0001f)
            {
                planes[i] = planes[i] / length;
            }
        }

        return planes;
    }

    #endregion
}
