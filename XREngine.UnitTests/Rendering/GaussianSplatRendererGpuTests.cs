using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenGL;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Gaussian;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Runs the sorted gaussian splat shaders on a real OpenGL context. The tests compile the engine shader
/// files and follow the dispatch plan of <see cref="GpuKeyIdRadixSort"/> and <see cref="GaussianSplatRenderer"/>:
/// depth keys, four radix passes, and then instanced quads with the renderer's blend and depth state.
/// </summary>
[TestFixture]
public sealed class GaussianSplatRendererGpuTests : GpuTestBase
{
    private const uint CulledKey = uint.MaxValue;

    [Test]
    public void GpuSplat_IsStd430_64Bytes()
    {
        Unsafe.SizeOf<GaussianSplatGpuSplat>().ShouldBe(64);
        Marshal.OffsetOf<GaussianSplatGpuSplat>(nameof(GaussianSplatGpuSplat.CenterOpacity)).ToInt32().ShouldBe(0);
        Marshal.OffsetOf<GaussianSplatGpuSplat>(nameof(GaussianSplatGpuSplat.Rotation)).ToInt32().ShouldBe(16);
        Marshal.OffsetOf<GaussianSplatGpuSplat>(nameof(GaussianSplatGpuSplat.ScalePad)).ToInt32().ShouldBe(32);
        Marshal.OffsetOf<GaussianSplatGpuSplat>(nameof(GaussianSplatGpuSplat.LinearColor)).ToInt32().ShouldBe(48);
    }

    [Test, Category("Hardware")]
    public void RadixSort_OneMillionPairs_AscendingAndStable()
    {
        const int count = 1_000_000;
        uint[] pairs = new uint[count * 2];
        uint state = 0x9E3779B9u;
        for (int i = 0; i < count; i++)
        {
            state = NextRandom(state);
            // 65,536 different keys that use all four bytes give long runs of equal keys.
            pairs[2 * i] = (state >> 16) * 0x00010001u;
            pairs[2 * i + 1] = (uint)i;
        }

        RunWithGLContext(gl =>
        {
            RequireHardwareCompute(gl);
            uint[] sorted = SortOnGpu(gl, pairs, count, out double milliseconds);
            TestContext.Out.WriteLine($"GPU key-id radix sort, {count} pairs: {milliseconds:F2} ms");
            AssertSortedPermutation(pairs, sorted, count, requireStable: true);
        }, timeoutMs: 120_000);
    }

    [Test, Category("Hardware")]
    public void RadixSort_TwoMillionPairs_Sorted()
    {
        const int count = 2_000_000;
        uint[] pairs = new uint[count * 2];
        uint state = 0x2545F491u;
        for (int i = 0; i < count; i++)
        {
            state = NextRandom(state);
            pairs[2 * i] = state;
            pairs[2 * i + 1] = (uint)i;
        }

        RunWithGLContext(gl =>
        {
            RequireHardwareCompute(gl);
            uint[] sorted = SortOnGpu(gl, pairs, count, out double milliseconds);
            TestContext.Out.WriteLine($"GPU key-id radix sort, {count} pairs: {milliseconds:F2} ms");
            AssertSortedPermutation(pairs, sorted, count, requireStable: true);
        }, timeoutMs: 120_000);
    }

    [Test, Category("Hardware")]
    public void Keys_OrderBackToFront()
    {
        // The slot after the visible count holds a splat that must sort last with the culled key.
        GaussianSplatGpuSplat[] splats =
        [
            Splat(new Vector3(0f, 0f, -1f), 1f, 0.1f, Vector3.One),
            Splat(new Vector3(0f, 0f, -5f), 1f, 0.1f, Vector3.One),
            Splat(new Vector3(0f, 0f, -3f), 1f, 0.1f, Vector3.One),
            Splat(new Vector3(0f, 0f, -2f), 1f, 0.1f, Vector3.One),
        ];

        RunWithGLContext(gl =>
        {
            RequireHardwareCompute(gl);
            uint order = PrepareOnGpu(gl, splats, visibleCount: 3u, Matrix4x4.Identity, out (uint Splats, uint VisibleCount) buffers);
            try
            {
                uint[] pairs = ReadStorage<uint>(gl, order, splats.Length * 2);
                uint[] ids = [pairs[1], pairs[3], pairs[5], pairs[7]];
                ids.ShouldBe([1u, 2u, 0u, 3u]);
                pairs[0].ShouldBeLessThan(pairs[2]);
                pairs[2].ShouldBeLessThan(pairs[4]);
                pairs[4].ShouldBeLessThan(CulledKey);
                pairs[6].ShouldBe(CulledKey);
            }
            finally
            {
                DeleteBuffers(gl, order, buffers.Splats, buffers.VisibleCount);
            }
        });
    }

    [Test, Category("Hardware")]
    public void Draw_PremultipliedOver_FrontColorWins()
    {
        const int size = 64;
        // Slot 0 is the front splat, so an unsorted draw puts the back splat over it.
        // Slots 2 and 3 are past the visible count and must not draw.
        GaussianSplatGpuSplat[] splats =
        [
            Splat(new Vector3(0f, 0f, -3f), 0.8f, 1f, new Vector3(0f, 1f, 0f)),
            Splat(new Vector3(0f, 0f, -5f), 1f, 1f, new Vector3(1f, 0f, 0f)),
            Splat(new Vector3(0f, 0f, -2f), 1f, 1f, Vector3.One),
            Splat(new Vector3(0f, 0f, -2f), 1f, 1f, Vector3.One),
        ];
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 100f);

        RunWithGLContext(gl =>
        {
            RequireHardwareCompute(gl);
            byte[] frame = DrawOnGpu(gl, splats, visibleCount: 2u, projection, size);
            int center = ((size / 2) * size + size / 2) * 4;
            TestContext.Out.WriteLine($"Center pixel RGBA: {frame[center]}, {frame[center + 1]}, {frame[center + 2]}, {frame[center + 3]}");

            // Premultiplied over, back to front: front = 0.8 green, back = 1.0 red.
            // Result = 0.8 * green + (1 - 0.8) * red.
            Math.Abs(frame[center] - 0.2f * 255f).ShouldBeLessThanOrEqualTo(2f, "red channel");
            Math.Abs(frame[center + 1] - 0.8f * 255f).ShouldBeLessThanOrEqualTo(2f, "green channel");
            ((float)frame[center + 2]).ShouldBeLessThanOrEqualTo(2f, "blue channel");
        });
    }

    [Test, Category("Hardware")]
    public void Draw_OffAxisSplatOutsideGuardBand_DoesNotSmearIntoView()
    {
        const int size = 64;
        // The center is at x/z = 3, far past the 1.3 x tan(fovX/2) guard band (0.75 for 60 degrees).
        // The unclamped Jacobian gives a 3-sigma extent of about 260 pixels, which reaches across the
        // whole view. The clamped Jacobian gives about 104 pixels, which stays outside the view.
        GaussianSplatGpuSplat[] splats = [Splat(new Vector3(6f, 0f, -2f), 1f, 1f, Vector3.One)];
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 100f);

        RunWithGLContext(gl =>
        {
            RequireHardwareCompute(gl);
            byte[] frame = DrawOnGpu(gl, splats, visibleCount: 1u, projection, size);
            int covered = 0;
            int brightest = 0;
            for (int i = 0; i < frame.Length; i += 4)
            {
                int channel = Math.Max(frame[i], Math.Max(frame[i + 1], frame[i + 2]));
                brightest = Math.Max(brightest, channel);
                if (channel > 0)
                    covered++;
            }

            TestContext.Out.WriteLine($"Covered pixels: {covered}, brightest channel: {brightest}");
            covered.ShouldBe(0, "An off-axis splat outside the guard band must not reach the view.");
        });
    }

    private static GaussianSplatGpuSplat Splat(Vector3 center, float opacity, float sigma, Vector3 linearColor)
        => new(center, opacity, Quaternion.Identity, new Vector3(sigma), linearColor);

    private static uint NextRandom(uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static void RequireHardwareCompute(GL gl)
    {
        AssertHardwareComputeOrInconclusive(gl);
        string vendor = gl.GetStringS(StringName.Vendor) ?? string.Empty;
        string renderer = gl.GetStringS(StringName.Renderer) ?? string.Empty;
        if (vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("GDI Generic", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"The splat renderer tests need a hardware OpenGL driver. Vendor='{vendor}', Renderer='{renderer}'.");
        }
    }

    /// <summary>
    /// Sorts the pairs on the GPU two times and returns the second result and its GPU time.
    /// The first sort warms up the driver.
    /// </summary>
    private static uint[] SortOnGpu(GL gl, uint[] input, int count, out double milliseconds)
    {
        (uint histogram, uint prefix, uint scatter) = CreateSortPrograms(gl);
        uint pairs = CreateStorage<uint>(gl, input);
        uint scratch = CreateStorage(gl, (nuint)input.Length * sizeof(uint));
        uint offsets = CreateStorage(gl, (nuint)GpuKeyIdRadixSort.GetOffsetCount((uint)count) * sizeof(uint));
        try
        {
            RunSortPasses(gl, histogram, prefix, scatter, pairs, scratch, offsets, (uint)count);
            gl.Finish();
            UploadStorage<uint>(gl, pairs, input);
            gl.Finish();

            long start = Stopwatch.GetTimestamp();
            RunSortPasses(gl, histogram, prefix, scatter, pairs, scratch, offsets, (uint)count);
            gl.Finish();
            milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            return ReadStorage<uint>(gl, pairs, count * 2);
        }
        finally
        {
            DeleteBuffers(gl, pairs, scratch, offsets);
            DeletePrograms(gl, histogram, prefix, scatter);
        }
    }

    /// <summary>
    /// Runs the depth key pass and the sort, as <see cref="GaussianSplatRenderer.Prepare(in Matrix4x4)"/> does.
    /// Returns the order buffer; the caller deletes it. The splat and count buffers are returned in
    /// <paramref name="splatAndCountBuffers"/>.
    /// </summary>
    private static uint PrepareOnGpu(GL gl, GaussianSplatGpuSplat[] splats, uint visibleCount, Matrix4x4 modelView, out (uint Splats, uint VisibleCount) splatAndCountBuffers)
    {
        uint capacity = (uint)splats.Length;
        uint keys = CompileComputeProgram(gl, GaussianSplatRenderer.KeysShaderPath);
        (uint histogram, uint prefix, uint scatter) = CreateSortPrograms(gl);
        uint splatBuffer = CreateStorage<GaussianSplatGpuSplat>(gl, splats);
        uint countBuffer = CreateStorage<uint>(gl, [visibleCount, 0u, 0u, 0u]);
        uint order = CreateStorage(gl, (nuint)capacity * GpuKeyIdRadixSort.UIntsPerPair * sizeof(uint));
        uint scratch = CreateStorage(gl, (nuint)capacity * GpuKeyIdRadixSort.UIntsPerPair * sizeof(uint));
        uint offsets = CreateStorage(gl, (nuint)GpuKeyIdRadixSort.GetOffsetCount(capacity) * sizeof(uint));
        try
        {
            gl.UseProgram(keys);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianSplatRenderer.SplatsBinding, splatBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianSplatRenderer.OrderBinding, order);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianSplatRenderer.VisibleCountBinding, countBuffer);
            gl.Uniform1(gl.GetUniformLocation(keys, GaussianSplatRenderer.CapacityUniform), capacity);
            SetMatrix(gl, keys, GaussianSplatRenderer.ModelViewUniform, modelView);
            gl.DispatchCompute(GaussianSplatRenderer.GetKeyGroupCount(capacity), 1u, 1u);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            RunSortPasses(gl, histogram, prefix, scatter, order, scratch, offsets, capacity);
            gl.Finish();

            splatAndCountBuffers = (splatBuffer, countBuffer);
            uint result = order;
            order = 0u;
            splatBuffer = 0u;
            countBuffer = 0u;
            return result;
        }
        finally
        {
            DeleteBuffers(gl, order, scratch, offsets, splatBuffer, countBuffer);
            DeletePrograms(gl, keys, histogram, prefix, scatter);
        }
    }

    /// <summary>
    /// Prepares the splats, draws them into an RGBA8 framebuffer with the renderer's quad shaders and
    /// render options, and returns the framebuffer: RGBA rows, bottom row first.
    /// </summary>
    private static byte[] DrawOnGpu(GL gl, GaussianSplatGpuSplat[] splats, uint visibleCount, Matrix4x4 projection, int size)
    {
        uint order = PrepareOnGpu(gl, splats, visibleCount, Matrix4x4.Identity, out (uint Splats, uint VisibleCount) buffers);
        uint program = CreateProgram(
            gl,
            LoadShaderSource(GaussianSplatRenderer.QuadVertexShaderPath),
            LoadShaderSource(GaussianSplatRenderer.QuadFragmentShaderPath));
        uint framebuffer = gl.GenFramebuffer();
        uint color = gl.GenTexture();
        uint depth = gl.GenRenderbuffer();
        uint vertexArray = gl.GenVertexArray();
        uint vertexBuffer = 0u;
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, color);
            gl.TexStorage2D(TextureTarget.Texture2D, 1u, SizedInternalFormat.Rgba8, (uint)size, (uint)size);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)size, (uint)size);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, color, 0);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
            gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer).ShouldBe(GLEnum.FramebufferComplete);

            gl.Viewport(0, 0, (uint)size, (uint)size);
            gl.DepthMask(true);
            gl.ClearColor(0f, 0f, 0f, 0f);
            gl.ClearDepth(1.0);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            ApplyRenderOptions(gl, GaussianSplatRenderer.CreateRenderOptions());

            // The quad corners, as the renderer's quad mesh has them: x and y from -1 to 1.
            float[] corners = [-1f, -1f, 0f, 1f, -1f, 0f, -1f, 1f, 0f, 1f, 1f, 0f];
            vertexBuffer = CreateArrayBuffer(gl, corners);
            gl.BindVertexArray(vertexArray);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vertexBuffer);
            gl.EnableVertexAttribArray(0u);
            unsafe
            {
                gl.VertexAttribPointer(0u, 3, VertexAttribPointerType.Float, false, 3u * sizeof(float), (void*)0);
            }

            gl.UseProgram(program);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianSplatRenderer.SplatsBinding, buffers.Splats);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianSplatRenderer.OrderBinding, order);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianSplatRenderer.VisibleCountBinding, buffers.VisibleCount);
            SetMatrix(gl, program, "ModelMatrix", Matrix4x4.Identity);
            SetMatrix(gl, program, "ViewMatrix_VTX", Matrix4x4.Identity);
            SetMatrix(gl, program, "ProjMatrix_VTX", projection);
            gl.Uniform1(gl.GetUniformLocation(program, "ScreenWidth"), (float)size);
            gl.Uniform1(gl.GetUniformLocation(program, "ScreenHeight"), (float)size);
            gl.Uniform1(gl.GetUniformLocation(program, GaussianSplatRenderer.SplatScaleUniform), 1f);
            gl.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4u, (uint)splats.Length);
            gl.Finish();

            byte[] rgba = new byte[size * size * 4];
            gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            gl.ReadPixels(0, 0, (uint)size, (uint)size, PixelFormat.Rgba, PixelType.UnsignedByte, rgba.AsSpan());
            gl.GetError().ShouldBe(GLEnum.NoError);
            return rgba;
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0u);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(color);
            gl.DeleteRenderbuffer(depth);
            gl.DeleteVertexArray(vertexArray);
            DeleteBuffers(gl, vertexBuffer, order, buffers.Splats, buffers.VisibleCount);
            DeletePrograms(gl, program);
        }
    }

    /// <summary>Applies the blend, depth, and cull state of the renderer's quad material.</summary>
    private static void ApplyRenderOptions(GL gl, RenderingParameters options)
    {
        BlendMode blend = options.BlendModeAllDrawBuffers.ShouldNotBeNull();
        blend.Enabled.ShouldBe(ERenderParamUsage.Enabled);
        gl.Enable(EnableCap.Blend);
        gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
        gl.BlendFuncSeparate(
            ToGl(blend.RgbSrcFactor),
            ToGl(blend.RgbDstFactor),
            ToGl(blend.AlphaSrcFactor),
            ToGl(blend.AlphaDstFactor));

        options.DepthTest.Enabled.ShouldBe(ERenderParamUsage.Enabled);
        options.DepthTest.Function.ShouldBe(EComparison.Lequal);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.DepthMask(options.DepthTest.UpdateDepth);

        options.CullMode.ShouldBe(ECullMode.None);
        gl.Disable(EnableCap.CullFace);
    }

    private static BlendingFactor ToGl(EBlendingFactor factor)
        => factor switch
        {
            EBlendingFactor.Zero => BlendingFactor.Zero,
            EBlendingFactor.One => BlendingFactor.One,
            EBlendingFactor.SrcAlpha => BlendingFactor.SrcAlpha,
            EBlendingFactor.OneMinusSrcAlpha => BlendingFactor.OneMinusSrcAlpha,
            _ => throw new AssertionException($"The test does not map blend factor {factor}."),
        };

    private static (uint Histogram, uint Prefix, uint Scatter) CreateSortPrograms(GL gl)
        => (CompileComputeProgram(gl, GpuKeyIdRadixSort.HistogramShaderPath),
            CompileComputeProgram(gl, GpuKeyIdRadixSort.PrefixShaderPath),
            CompileComputeProgram(gl, GpuKeyIdRadixSort.ScatterShaderPath));

    /// <summary>Runs the radix passes in the order and with the bindings that <see cref="GpuKeyIdRadixSort.Sort"/> uses.</summary>
    private static void RunSortPasses(GL gl, uint histogram, uint prefix, uint scatter, uint pairs, uint scratch, uint offsets, uint count)
    {
        uint blockCount = GpuKeyIdRadixSort.GetBlockCount(count);
        for (int pass = 0; pass < GpuKeyIdRadixSort.PassCount; pass++)
        {
            uint source = GpuKeyIdRadixSort.PassReadsPairs(pass) ? pairs : scratch;
            uint destination = GpuKeyIdRadixSort.PassReadsPairs(pass) ? scratch : pairs;
            uint shift = GpuKeyIdRadixSort.GetPassShift(pass);

            gl.UseProgram(histogram);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GpuKeyIdRadixSort.InputBinding, source);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GpuKeyIdRadixSort.OffsetsBinding, offsets);
            gl.Uniform1(gl.GetUniformLocation(histogram, GpuKeyIdRadixSort.CountUniform), count);
            gl.Uniform1(gl.GetUniformLocation(histogram, GpuKeyIdRadixSort.ShiftUniform), shift);
            gl.DispatchCompute(blockCount, 1u, 1u);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            gl.UseProgram(prefix);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GpuKeyIdRadixSort.OffsetsBinding, offsets);
            gl.Uniform1(gl.GetUniformLocation(prefix, GpuKeyIdRadixSort.BlockCountUniform), blockCount);
            gl.DispatchCompute(1u, 1u, 1u);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            gl.UseProgram(scatter);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GpuKeyIdRadixSort.InputBinding, source);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GpuKeyIdRadixSort.OutputBinding, destination);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GpuKeyIdRadixSort.OffsetsBinding, offsets);
            gl.Uniform1(gl.GetUniformLocation(scatter, GpuKeyIdRadixSort.CountUniform), count);
            gl.Uniform1(gl.GetUniformLocation(scatter, GpuKeyIdRadixSort.ShiftUniform), shift);
            gl.DispatchCompute(blockCount, 1u, 1u);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
        }
    }

    /// <summary>
    /// Checks that the output keys ascend, that every input pair occurs one time, and (when
    /// <paramref name="requireStable"/> is set) that equal keys keep ascending ids.
    /// </summary>
    private static void AssertSortedPermutation(uint[] input, uint[] output, int count, bool requireStable)
    {
        bool[] seen = new bool[count];
        for (int i = 0; i < count; i++)
        {
            uint key = output[2 * i];
            uint id = output[2 * i + 1];
            if (id >= (uint)count)
                Assert.Fail($"Output {i} has id {id}, which is out of range.");
            if (seen[id])
                Assert.Fail($"Output {i} repeats id {id}.");
            seen[id] = true;
            if (input[2 * id] != key)
                Assert.Fail($"Output {i} pairs id {id} with key {key:X8}; the input key is {input[2 * id]:X8}.");

            if (i == 0)
                continue;

            uint previousKey = output[2 * (i - 1)];
            if (previousKey > key)
                Assert.Fail($"Keys descend at output {i}: {previousKey:X8} then {key:X8}.");
            if (requireStable && previousKey == key && output[2 * (i - 1) + 1] > id)
                Assert.Fail($"Equal keys {key:X8} change id order at output {i}.");
        }
    }

    private static uint CompileComputeProgram(GL gl, string relativePath)
    {
        uint shader = CompileComputeShader(gl, LoadShaderSource(relativePath));
        try
        {
            return CreateComputeProgram(gl, shader);
        }
        finally
        {
            gl.DeleteShader(shader);
        }
    }

    private static unsafe void SetMatrix(GL gl, uint program, string name, Matrix4x4 value)
    {
        int location = gl.GetUniformLocation(program, name);
        gl.UniformMatrix4(location, 1u, false, (float*)&value);
    }

    private static unsafe uint CreateStorage<T>(GL gl, ReadOnlySpan<T> data) where T : unmanaged
    {
        uint buffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        fixed (T* pointer = data)
            gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(data.Length * sizeof(T)), pointer, BufferUsageARB.DynamicCopy);
        return buffer;
    }

    private static unsafe uint CreateStorage(GL gl, nuint byteCount)
    {
        uint buffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        gl.BufferData(BufferTargetARB.ShaderStorageBuffer, byteCount, null, BufferUsageARB.DynamicCopy);
        return buffer;
    }

    private static unsafe void UploadStorage<T>(GL gl, uint buffer, ReadOnlySpan<T> data) where T : unmanaged
    {
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        fixed (T* pointer = data)
            gl.BufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(data.Length * sizeof(T)), pointer);
    }

    private static unsafe T[] ReadStorage<T>(GL gl, uint buffer, int count) where T : unmanaged
    {
        T[] result = new T[count];
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        fixed (T* pointer = result)
            gl.GetBufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(count * sizeof(T)), pointer);
        return result;
    }

    private static unsafe uint CreateArrayBuffer(GL gl, float[] data)
    {
        uint buffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
        fixed (float* pointer = data)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), pointer, BufferUsageARB.StaticDraw);
        return buffer;
    }

    private static void DeleteBuffers(GL gl, params uint[] buffers)
    {
        foreach (uint buffer in buffers)
        {
            if (buffer != 0u)
                gl.DeleteBuffer(buffer);
        }
    }

    private static void DeletePrograms(GL gl, params uint[] programs)
    {
        foreach (uint program in programs)
        {
            if (program != 0u)
                gl.DeleteProgram(program);
        }
    }
}
