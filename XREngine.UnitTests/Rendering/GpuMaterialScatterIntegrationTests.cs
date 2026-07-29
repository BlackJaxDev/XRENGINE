using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenGL;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Executes the production compact material-scatter shader against boundary-sized buffers.
/// </summary>
[TestFixture]
public sealed class GpuMaterialScatterIntegrationTests : GpuTestBase
{
    private const int DrawMetadataUInts = 16;
    private const int MeshDataUInts = 4;
    private const int SortKeyUInts = 4;
    private const int DrawCommandUInts = 5;
    private const int MaterialTierCount = 3;
    private const uint GuardValue = 0xC0FFEE11u;

    private new static void AssertHardwareComputeOrInconclusive(GL gl)
    {
        string vendor = gl.GetStringS(StringName.Vendor) ?? string.Empty;
        string renderer = gl.GetStringS(StringName.Renderer) ?? string.Empty;
        if (vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("GDI Generic", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive(
                $"GPU compute integration tests require a hardware OpenGL driver. Vendor='{vendor}', Renderer='{renderer}'.");
        }
    }

    [Test]
    public unsafe void CompactScatter_EmptyExactAndOverflow_ClampCountsAndPreserveGuards()
    {
        var (gl, window) = CreateGLContext();
        if (gl is null || window is null)
        {
            Assert.Inconclusive("Could not create OpenGL context.");
            return;
        }

        try
        {
            AssertHardwareComputeOrInconclusive(gl);
            string shaderPath = Path.Combine(
                ShaderBasePath,
                "Compute",
                "Indirect",
                "GPURenderMaterialScatter.comp");
            File.Exists(shaderPath).ShouldBeTrue($"Shader file not found: {shaderPath}");

            uint shader = CompileComputeShader(gl, File.ReadAllText(shaderPath));
            uint program = CreateComputeProgram(gl, shader);
            try
            {
                RunCase("empty", [], 1, [0u, 0u, 0u], expectOverflow: false);
                RunCase("exact-capacity", [0, 1, 2], 1, [1u, 1u, 1u], expectOverflow: false);
                RunCase("overflow", [0, 0, 0, 0], 2, [2u, 0u, 0u], expectOverflow: true);
            }
            finally
            {
                gl.DeleteProgram(program);
                gl.DeleteShader(shader);
            }

            void RunCase(
                string name,
                int[] tiers,
                int maxDrawsPerBucket,
                uint[] expectedCounts,
                bool expectOverflow)
            {
                int inputCount = tiers.Length;
                int allocatedInputCount = Math.Max(1, inputCount);
                List<uint> buffers = [];

                uint CreateBuffer(uint binding, uint[] data)
                {
                    uint buffer = gl.GenBuffer();
                    buffers.Add(buffer);
                    gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
                    gl.BufferData<uint>(
                        BufferTargetARB.ShaderStorageBuffer,
                        data.AsSpan(),
                        BufferUsageARB.DynamicCopy);
                    gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, binding, buffer);
                    return buffer;
                }

                try
                {
                    uint[] metadata = new uint[allocatedInputCount * DrawMetadataUInts];
                    uint[] sortKeys = new uint[allocatedInputCount * SortKeyUInts];
                    uint[] lodTransitions = new uint[allocatedInputCount * MeshDataUInts];
                    uint[] transparencyMetadata = new uint[allocatedInputCount * MeshDataUInts];
                    for (int i = 0; i < inputCount; i++)
                    {
                        metadata[i * DrawMetadataUInts + 0] = (uint)i;
                        metadata[i * DrawMetadataUInts + 1] = (uint)tiers[i];
                        metadata[i * DrawMetadataUInts + 3] = 0u;
                        metadata[i * DrawMetadataUInts + 11] = 1u;
                        metadata[i * DrawMetadataUInts + 12] = 0u;
                        sortKeys[i * SortKeyUInts + 3] = (uint)i;
                    }

                    uint[] meshData = new uint[MaterialTierCount * MeshDataUInts];
                    for (int tier = 0; tier < MaterialTierCount; tier++)
                    {
                        meshData[tier * MeshDataUInts + 0] = 3u;
                        meshData[tier * MeshDataUInts + 1] = 0u;
                        meshData[tier * MeshDataUInts + 2] = 0u;
                        meshData[tier * MeshDataUInts + 3] = (uint)tier;
                    }

                    int usableIndirectUInts =
                        MaterialTierCount * maxDrawsPerBucket * DrawCommandUInts;
                    uint[] indirect = Enumerable.Repeat(
                        GuardValue,
                        usableIndirectUInts + DrawCommandUInts).ToArray();
                    uint[] drawCounts = [0u, 0u, 0u, GuardValue];
                    uint[] overflow = [0u, GuardValue];
                    uint[] stats = new uint[41];
                    stats[^1] = GuardValue;

                    CreateBuffer(0, metadata);
                    CreateBuffer(1, meshData);
                    CreateBuffer(2, [(uint)inputCount, 0u, 0u]);
                    CreateBuffer(3, sortKeys);
                    CreateBuffer(4, [0u]);
                    uint indirectBuffer = CreateBuffer(5, indirect);
                    uint drawCountBuffer = CreateBuffer(6, drawCounts);
                    uint overflowBuffer = CreateBuffer(7, overflow);
                    CreateBuffer(8, lodTransitions);
                    uint statsBuffer = CreateBuffer(9, stats);
                    CreateBuffer(10, transparencyMetadata);

                    gl.UseProgram(program);
                    gl.Uniform1(gl.GetUniformLocation(program, "CurrentRenderPass"), 0);
                    gl.Uniform1(gl.GetUniformLocation(program, "MaxMaterialSlotLookup"), 1);
                    gl.Uniform1(gl.GetUniformLocation(program, "MaxBucketCount"), MaterialTierCount);
                    gl.Uniform1(
                        gl.GetUniformLocation(program, "MaxIndirectDrawsPerBucket"),
                        maxDrawsPerBucket);
                    gl.Uniform3(gl.GetUniformLocation(program, "AtlasIndexCounts"), 3u, 3u, 3u);
                    gl.Uniform3(gl.GetUniformLocation(program, "AtlasVertexCounts"), 1u, 1u, 1u);
                    gl.Uniform1(gl.GetUniformLocation(program, "StatsEnabled"), 1u);
                    gl.Uniform1(gl.GetUniformLocation(program, "RejectExactTransparentMultiview"), 0u);
                    gl.Uniform1(gl.GetUniformLocation(program, "CompactMaterialTableOutput"), 1u);

                    gl.DispatchCompute(1u, 1u, 1u);
                    gl.MemoryBarrier(
                        MemoryBarrierMask.ShaderStorageBarrierBit |
                        MemoryBarrierMask.CommandBarrierBit);
                    gl.Finish();

                    gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, drawCountBuffer);
                    uint* countPointer = (uint*)gl.MapBuffer(
                        BufferTargetARB.ShaderStorageBuffer,
                        BufferAccessARB.ReadOnly);
                    ((nint)countPointer).ShouldNotBe(nint.Zero, $"{name}: count mapping failed");
                    for (int tier = 0; tier < MaterialTierCount; tier++)
                        countPointer[tier].ShouldBe(expectedCounts[tier], $"{name}: tier {tier}");
                    countPointer[MaterialTierCount].ShouldBe(GuardValue, $"{name}: count guard");
                    gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

                    gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, overflowBuffer);
                    uint* overflowPointer = (uint*)gl.MapBuffer(
                        BufferTargetARB.ShaderStorageBuffer,
                        BufferAccessARB.ReadOnly);
                    ((nint)overflowPointer).ShouldNotBe(nint.Zero, $"{name}: overflow mapping failed");
                    overflowPointer[0].ShouldBe(expectOverflow ? 1u : 0u, $"{name}: overflow flag");
                    overflowPointer[1].ShouldBe(GuardValue, $"{name}: overflow guard");
                    gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

                    gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, indirectBuffer);
                    uint* indirectPointer = (uint*)gl.MapBuffer(
                        BufferTargetARB.ShaderStorageBuffer,
                        BufferAccessARB.ReadOnly);
                    ((nint)indirectPointer).ShouldNotBe(nint.Zero, $"{name}: indirect mapping failed");
                    for (int i = usableIndirectUInts; i < indirect.Length; i++)
                        indirectPointer[i].ShouldBe(GuardValue, $"{name}: indirect guard {i}");
                    gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

                    gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, statsBuffer);
                    uint* statsPointer = (uint*)gl.MapBuffer(
                        BufferTargetARB.ShaderStorageBuffer,
                        BufferAccessARB.ReadOnly);
                    ((nint)statsPointer).ShouldNotBe(nint.Zero, $"{name}: stats mapping failed");
                    statsPointer[28].ShouldBe(
                        expectedCounts[0] + expectedCounts[1] + expectedCounts[2],
                        $"{name}: emitted count");
                    statsPointer[40].ShouldBe(GuardValue, $"{name}: stats guard");
                    gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

                    gl.GetError().ShouldBe(GLEnum.NoError, $"{name}: OpenGL validation error");
                }
                finally
                {
                    foreach (uint buffer in buffers)
                        gl.DeleteBuffer(buffer);
                }
            }
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }
}
