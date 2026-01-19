using NUnit.Framework;
using Shouldly;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Integration tests that compile and run the SurfelGI compute shaders.
///
/// These tests create a real OpenGL 4.6 context (hidden window), compile the actual shader files,
/// set up GPU buffers and textures with deterministic data, dispatch compute shaders, and validate
/// SSBO side effects.
/// </summary>
[TestFixture]
public class SurfelGiComputeIntegrationTests
{
    private const int Width = 64;
    private const int Height = 64;

    private static bool IsHeadless =>
        Environment.GetEnvironmentVariable("XR_HEADLESS_TEST") == "1" ||
        Environment.GetEnvironmentVariable("CI") == "true";

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

    private static (GL?, IWindow?) CreateGLContext()
    {
        if (IsHeadless)
            return (null, null);

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(Width, Height);
        options.IsVisible = false;
        options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));

        IWindow? window = null;
        GL? gl = null;

        try
        {
            window = Window.Create(options);
            window.Initialize();
            window.MakeCurrent();
            window.DoEvents();
            gl = GL.GetApi(window);
        }
        catch
        {
            window?.Close();
            window?.Dispose();
            return (null, null);
        }

        return (gl, window);
    }

    private static uint CompileComputeShader(GL gl, string source)
    {
        uint shader = gl.CreateShader(ShaderType.ComputeShader);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);

        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
        {
            string infoLog = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"Failed to compile compute shader:\n{infoLog}");
        }

        return shader;
    }

    private static uint CreateComputeProgram(GL gl, uint computeShader)
    {
        uint program = gl.CreateProgram();
        gl.AttachShader(program, computeShader);
        gl.LinkProgram(program);

        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int status);
        if (status == 0)
        {
            string infoLog = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            throw new InvalidOperationException($"Failed to link compute program:\n{infoLog}");
        }

        return program;
    }

    private static string ShaderPath(params string[] parts)
        => Path.Combine([ShaderBasePath, ..parts]);

    private static void InconclusiveIfMissing(string path)
    {
        if (!File.Exists(path))
            Assert.Inconclusive($"Shader file not found: {path}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SurfelGpu
    {
        public Vector4 PosRadius;
        public Vector4 Normal;
        public Vector4 Albedo;
        public uint MetaX;
        public uint MetaY;
        public uint MetaZ;
        public uint MetaW;
    }

    private static unsafe uint CreateSsbo(GL gl, nuint sizeBytes, BufferUsageARB usage, uint bindingIndex)
    {
        uint buffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        gl.BufferData(BufferTargetARB.ShaderStorageBuffer, sizeBytes, null, usage);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, bindingIndex, buffer);
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        return buffer;
    }

    private static unsafe void UploadSsbo<T>(GL gl, uint buffer, ReadOnlySpan<T> data) where T : unmanaged
    {
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        fixed (T* ptr = data)
        {
            gl.BufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(data.Length * sizeof(T)), ptr);
        }
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private static unsafe void WriteIntToSsbo(GL gl, uint buffer, int value)
    {
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        int* ptr = (int*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadWrite);
        ptr[0] = value;
        gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private static unsafe int ReadIntFromSsbo(GL gl, uint buffer)
    {
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        int* ptr = (int*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
        int v = ptr[0];
        gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        return v;
    }

    private static unsafe uint ReadUIntFromSsbo(GL gl, uint buffer, nuint index)
    {
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        uint* ptr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
        uint v = ptr[index];
        gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        return v;
    }

    private static unsafe SurfelGpu ReadSurfel(GL gl, uint surfelBuffer, nuint index)
    {
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, surfelBuffer);
        SurfelGpu* ptr = (SurfelGpu*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
        SurfelGpu s = ptr[index];
        gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        return s;
    }

    private static unsafe void WriteSurfel(GL gl, uint surfelBuffer, nuint index, SurfelGpu surfel)
    {
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, surfelBuffer);
        SurfelGpu* ptr = (SurfelGpu*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadWrite);
        ptr[index] = surfel;
        gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private static unsafe uint CreateFloatTexture2D(GL gl, int width, int height, Vector4 fill)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        var data = new float[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            int o = i * 4;
            data[o + 0] = fill.X;
            data[o + 1] = fill.Y;
            data[o + 2] = fill.Z;
            data[o + 3] = fill.W;
        }

        fixed (float* p = data)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba32f, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.Float, p);
        }

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private static unsafe uint CreateDepthTexture2D(GL gl, int width, int height, float fillDepth)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        var data = new float[width * height];
        Array.Fill(data, fillDepth);

        fixed (float* p = data)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.R32f, (uint)width, (uint)height, 0, PixelFormat.Red, PixelType.Float, p);
        }

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private static unsafe uint CreateUIntTexture2D(GL gl, int width, int height, uint fill)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        var data = new uint[width * height];
        Array.Fill(data, fill);

        fixed (uint* p = data)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.R32ui, (uint)width, (uint)height, 0, PixelFormat.RedInteger, PixelType.UnsignedInt, p);
        }

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private static unsafe uint CreateOutputTexture2D(GL gl, int width, int height)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.Float, null);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private static void BindTextureUnit(GL gl, uint texture, int unit)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    private static void SetUniform(GL gl, uint program, string name, int value)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc >= 0)
            gl.Uniform1(loc, value);
    }

    private static void SetUniform(GL gl, uint program, string name, uint value)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc >= 0)
            gl.Uniform1(loc, value);
    }

    private static void SetUniform(GL gl, uint program, string name, bool value)
        => SetUniform(gl, program, name, value ? 1 : 0);

    private static void SetUniform(GL gl, uint program, string name, float value)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc >= 0)
            gl.Uniform1(loc, value);
    }

    private static unsafe void SetUniform(GL gl, uint program, string name, Vector3 value)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc >= 0)
            gl.Uniform3(loc, value.X, value.Y, value.Z);
    }

    private static unsafe void SetUniform(GL gl, uint program, string name, Vector2 value)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc >= 0)
            gl.Uniform2(loc, value.X, value.Y);
    }

    private static void SetUniformIVec2(GL gl, uint program, string name, int x, int y)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc >= 0)
            gl.Uniform2(loc, x, y);
    }

    private static unsafe void SetUniformUVec3(GL gl, uint program, string name, uint x, uint y, uint z)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc >= 0)
            gl.Uniform3(loc, x, y, z);
    }

    private static unsafe void SetUniformMat4(GL gl, uint program, string name, Matrix4x4 mat)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc < 0)
            return;

        Span<float> m =
        [
            mat.M11, mat.M12, mat.M13, mat.M14,
            mat.M21, mat.M22, mat.M23, mat.M24,
            mat.M31, mat.M32, mat.M33, mat.M34,
            mat.M41, mat.M42, mat.M43, mat.M44,
        ];

        fixed (float* p = m)
        {
            gl.UniformMatrix4(loc, 1, false, p);
        }
    }

    [TestCase("Init.comp")]
    [TestCase("Recycle.comp")]
    [TestCase("ResetGrid.comp")]
    [TestCase("BuildGrid.comp")]
    [TestCase("Spawn.comp")]
    [TestCase("Shade.comp")]
    public void SurfelGI_ComputeShader_CompilesAndLinks(string shaderFile)
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = ShaderPath("Compute", "SurfelGI", shaderFile);
            InconclusiveIfMissing(shaderPath);

            uint shader = CompileComputeShader(gl, File.ReadAllText(shaderPath));
            shader.ShouldBeGreaterThan(0u);

            uint program = CreateComputeProgram(gl, shader);
            program.ShouldBeGreaterThan(0u);

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
    public unsafe void SurfelGI_Init_Dispatch_InitializesFreeStackAndCounters()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        const uint maxSurfels = 128u;

        try
        {
            string shaderPath = ShaderPath("Compute", "SurfelGI", "Init.comp");
            InconclusiveIfMissing(shaderPath);

            uint shader = CompileComputeShader(gl, File.ReadAllText(shaderPath));
            uint program = CreateComputeProgram(gl, shader);

            uint surfelBuffer = CreateSsbo(gl, (nuint)(maxSurfels * (uint)Marshal.SizeOf<SurfelGpu>()), BufferUsageARB.DynamicDraw, 0);
            uint counterBuffer = CreateSsbo(gl, 16, BufferUsageARB.DynamicDraw, 1);
            uint freeStackBuffer = CreateSsbo(gl, (nuint)(maxSurfels * sizeof(uint)), BufferUsageARB.DynamicDraw, 2);

            gl.UseProgram(program);
            SetUniform(gl, program, "maxSurfels", maxSurfels);
            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            ReadIntFromSsbo(gl, counterBuffer).ShouldBe((int)maxSurfels);
            ReadUIntFromSsbo(gl, freeStackBuffer, 0).ShouldBe(0u);
            ReadUIntFromSsbo(gl, freeStackBuffer, maxSurfels - 1u).ShouldBe(maxSurfels - 1u);

            var s0 = ReadSurfel(gl, surfelBuffer, 0);
            s0.MetaY.ShouldBe(0u);
            s0.PosRadius.ShouldBe(Vector4.Zero);
            s0.Normal.ShouldBe(Vector4.Zero);
            s0.Albedo.ShouldBe(Vector4.Zero);

            gl.DeleteBuffer(surfelBuffer);
            gl.DeleteBuffer(counterBuffer);
            gl.DeleteBuffer(freeStackBuffer);
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
    public unsafe void SurfelGI_ResetGrid_Dispatch_ZeroesCounts()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        const uint cellCount = 64u;

        try
        {
            string shaderPath = ShaderPath("Compute", "SurfelGI", "ResetGrid.comp");
            InconclusiveIfMissing(shaderPath);

            uint shader = CompileComputeShader(gl, File.ReadAllText(shaderPath));
            uint program = CreateComputeProgram(gl, shader);

            uint gridCounts = CreateSsbo(gl, (nuint)(cellCount * sizeof(uint)), BufferUsageARB.DynamicDraw, 3);
            var seed = new uint[cellCount];
            for (int i = 0; i < seed.Length; i++)
                seed[i] = 123u;
            UploadSsbo(gl, gridCounts, seed);

            gl.UseProgram(program);
            SetUniform(gl, program, "cellCount", cellCount);
            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            ReadUIntFromSsbo(gl, gridCounts, 0).ShouldBe(0u);
            ReadUIntFromSsbo(gl, gridCounts, cellCount / 2u).ShouldBe(0u);
            ReadUIntFromSsbo(gl, gridCounts, cellCount - 1u).ShouldBe(0u);

            gl.DeleteBuffer(gridCounts);
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
    public unsafe void SurfelGI_BuildGrid_Dispatch_InsertsActiveSurfelsIntoCells()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        const uint maxSurfels = 32u;
        const uint gridX = 4u;
        const uint gridY = 4u;
        const uint gridZ = 4u;
        const uint maxPerCell = 4u;
        const uint cellCount = gridX * gridY * gridZ;

        try
        {
            string shaderPath = ShaderPath("Compute", "SurfelGI", "BuildGrid.comp");
            InconclusiveIfMissing(shaderPath);

            uint shader = CompileComputeShader(gl, File.ReadAllText(shaderPath));
            uint program = CreateComputeProgram(gl, shader);

            uint surfelBuffer = CreateSsbo(gl, (nuint)(maxSurfels * (uint)Marshal.SizeOf<SurfelGpu>()), BufferUsageARB.DynamicDraw, 0);
            uint gridCounts = CreateSsbo(gl, (nuint)(cellCount * sizeof(uint)), BufferUsageARB.DynamicDraw, 3);
            uint gridIndices = CreateSsbo(gl, (nuint)(cellCount * maxPerCell * sizeof(uint)), BufferUsageARB.DynamicDraw, 4);

            // Ensure deterministic behavior: the shader iterates up to maxSurfels and any
            // uninitialized surfel data could be treated as active on some drivers.
            UploadSsbo(gl, surfelBuffer, new SurfelGpu[(int)maxSurfels]);

            // Seed counts to 0 and indices to sentinel.
            UploadSsbo(gl, gridCounts, new uint[cellCount]);
            var sentinel = new uint[cellCount * maxPerCell];
            for (int i = 0; i < sentinel.Length; i++)
                sentinel[i] = 0xDEADBEEFu;
            UploadSsbo(gl, gridIndices, sentinel);

            // Active surfels in-bounds.
            WriteSurfel(gl, surfelBuffer, 0, new SurfelGpu
            {
                PosRadius = new Vector4(0.1f, 0.1f, 0.1f, 0.1f),
                Normal = new Vector4(0, 0, 1, 0),
                Albedo = new Vector4(1, 0, 0, 1),
                MetaX = 0,
                MetaY = 1,
                MetaZ = 0,
                MetaW = 0,
            });
            WriteSurfel(gl, surfelBuffer, 1, new SurfelGpu
            {
                PosRadius = new Vector4(1.1f, 0.1f, 0.1f, 0.1f),
                Normal = new Vector4(0, 0, 1, 0),
                Albedo = new Vector4(0, 1, 0, 1),
                MetaX = 0,
                MetaY = 1,
                MetaZ = 0,
                MetaW = 0,
            });
            // Inactive surfel should not be inserted.
            WriteSurfel(gl, surfelBuffer, 2, new SurfelGpu
            {
                PosRadius = new Vector4(0.2f, 0.2f, 0.2f, 0.1f),
                Normal = new Vector4(0, 0, 1, 0),
                Albedo = new Vector4(0, 0, 1, 1),
                MetaX = 0,
                MetaY = 0,
                MetaZ = 0,
                MetaW = 0,
            });
            // Out-of-bounds surfel should not be inserted.
            WriteSurfel(gl, surfelBuffer, 3, new SurfelGpu
            {
                PosRadius = new Vector4(1000f, 0, 0, 0.1f),
                Normal = new Vector4(0, 0, 1, 0),
                Albedo = new Vector4(1, 1, 1, 1),
                MetaX = 0,
                MetaY = 1,
                MetaZ = 0,
                MetaW = 0,
            });

            gl.UseProgram(program);
            SetUniform(gl, program, "maxSurfels", maxSurfels);
            SetUniform(gl, program, "hasCulledCommands", false);
            SetUniform(gl, program, "culledFloatCount", 0u);
            SetUniform(gl, program, "culledCommandFloats", 48u);
            SetUniform(gl, program, "gridOrigin", Vector3.Zero);
            SetUniform(gl, program, "cellSize", 1f);
            SetUniformUVec3(gl, program, "gridDim", gridX, gridY, gridZ);
            SetUniform(gl, program, "maxPerCell", maxPerCell);

            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // Cell (0,0,0) index = 0.
            ReadUIntFromSsbo(gl, gridCounts, 0).ShouldBe(1u);
            // Cell (1,0,0) index = 1.
            ReadUIntFromSsbo(gl, gridCounts, 1).ShouldBe(1u);

            uint idx0 = ReadUIntFromSsbo(gl, gridIndices, 0);
            uint idx1 = ReadUIntFromSsbo(gl, gridIndices, maxPerCell);
            idx0.ShouldBe(0u);
            idx1.ShouldBe(1u);

            gl.DeleteBuffer(surfelBuffer);
            gl.DeleteBuffer(gridCounts);
            gl.DeleteBuffer(gridIndices);
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
    public unsafe void SurfelGI_BuildGrid_Dispatch_Overflow_IncrementsCountsButDoesNotWritePastMaxPerCell()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        const uint maxSurfels = 8u;
        const uint gridX = 1u;
        const uint gridY = 1u;
        const uint gridZ = 1u;
        const uint maxPerCell = 2u;
        const uint cellCount = 1u;
        const uint activeCount = 5u;

        try
        {
            string shaderPath = ShaderPath("Compute", "SurfelGI", "BuildGrid.comp");
            InconclusiveIfMissing(shaderPath);

            uint shader = CompileComputeShader(gl, File.ReadAllText(shaderPath));
            uint program = CreateComputeProgram(gl, shader);

            uint surfelBuffer = CreateSsbo(gl, (nuint)(maxSurfels * (uint)Marshal.SizeOf<SurfelGpu>()), BufferUsageARB.DynamicDraw, 0);
            uint gridCounts = CreateSsbo(gl, (nuint)(cellCount * sizeof(uint)), BufferUsageARB.DynamicDraw, 3);
            // Allocate a bit more than needed and fill with sentinel so we can detect accidental extra writes.
            uint extraSentinel = 8u;
            uint gridIndices = CreateSsbo(gl, (nuint)((cellCount * maxPerCell + extraSentinel) * sizeof(uint)), BufferUsageARB.DynamicDraw, 4);

            UploadSsbo(gl, surfelBuffer, new SurfelGpu[(int)maxSurfels]);
            UploadSsbo(gl, gridCounts, new uint[cellCount]);

            var sentinel = new uint[cellCount * maxPerCell + extraSentinel];
            for (int i = 0; i < sentinel.Length; i++)
                sentinel[i] = 0xDEADBEEFu;
            UploadSsbo(gl, gridIndices, sentinel);

            // Make 5 active surfels all fall into the single cell (0,0,0).
            for (uint i = 0; i < activeCount; i++)
            {
                WriteSurfel(gl, surfelBuffer, i, new SurfelGpu
                {
                    PosRadius = new Vector4(1.0f + i * 0.01f, 1.0f, 1.0f, 0.1f),
                    Normal = new Vector4(0, 0, 1, 0),
                    Albedo = new Vector4(1, 1, 1, 1),
                    MetaX = 0,
                    MetaY = 1,
                    MetaZ = 0,
                    MetaW = 0,
                });
            }

            gl.UseProgram(program);
            SetUniform(gl, program, "maxSurfels", maxSurfels);
            SetUniform(gl, program, "hasCulledCommands", false);
            SetUniform(gl, program, "culledFloatCount", 0u);
            SetUniform(gl, program, "culledCommandFloats", 48u);
            SetUniform(gl, program, "gridOrigin", Vector3.Zero);
            SetUniform(gl, program, "cellSize", 10f);
            SetUniformUVec3(gl, program, "gridDim", gridX, gridY, gridZ);
            SetUniform(gl, program, "maxPerCell", maxPerCell);

            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // BuildGrid increments counts even when a cell overflows; it just stops writing indices.
            ReadUIntFromSsbo(gl, gridCounts, 0).ShouldBe(activeCount);

            uint idxA = ReadUIntFromSsbo(gl, gridIndices, 0);
            uint idxB = ReadUIntFromSsbo(gl, gridIndices, 1);
            idxA.ShouldBeLessThan(activeCount);
            idxB.ShouldBeLessThan(activeCount);

            // Past maxPerCell, nothing should have been written.
            for (nuint i = maxPerCell; i < maxPerCell + extraSentinel; i++)
                ReadUIntFromSsbo(gl, gridIndices, i).ShouldBe(0xDEADBEEFu);

            gl.DeleteBuffer(surfelBuffer);
            gl.DeleteBuffer(gridCounts);
            gl.DeleteBuffer(gridIndices);
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
    public unsafe void SurfelGI_BuildGrid_Dispatch_WhenHasCulledCommands_UsesWorldMatrixForCellBinning()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        const uint maxSurfels = 4u;
        const uint gridX = 4u;
        const uint gridY = 1u;
        const uint gridZ = 1u;
        const uint maxPerCell = 4u;
        const uint cellCount = gridX * gridY * gridZ;
        const float tx = 1.0f;

        try
        {
            string shaderPath = ShaderPath("Compute", "SurfelGI", "BuildGrid.comp");
            InconclusiveIfMissing(shaderPath);

            uint shader = CompileComputeShader(gl, File.ReadAllText(shaderPath));
            uint program = CreateComputeProgram(gl, shader);

            uint surfelBuffer = CreateSsbo(gl, (nuint)(maxSurfels * (uint)Marshal.SizeOf<SurfelGpu>()), BufferUsageARB.DynamicDraw, 0);
            uint gridCounts = CreateSsbo(gl, (nuint)(cellCount * sizeof(uint)), BufferUsageARB.DynamicDraw, 3);
            uint gridIndices = CreateSsbo(gl, (nuint)(cellCount * maxPerCell * sizeof(uint)), BufferUsageARB.DynamicDraw, 4);
            uint culledCommands = CreateSsbo(gl, (nuint)(48u * sizeof(float)), BufferUsageARB.DynamicDraw, 5);

            UploadSsbo(gl, surfelBuffer, new SurfelGpu[(int)maxSurfels]);
            UploadSsbo(gl, gridCounts, new uint[cellCount]);
            UploadSsbo(gl, gridIndices, new uint[cellCount * maxPerCell]);

            // Provide a single world matrix at culled[0..15] in row-major order.
            // Translation in X by +1 moves local cell (0,0,0) into world cell (1,0,0).
            var culled = new float[48];
            culled[0] = 1f; culled[1] = 0f; culled[2] = 0f; culled[3] = tx;
            culled[4] = 0f; culled[5] = 1f; culled[6] = 0f; culled[7] = 0f;
            culled[8] = 0f; culled[9] = 0f; culled[10] = 1f; culled[11] = 0f;
            culled[12] = 0f; culled[13] = 0f; culled[14] = 0f; culled[15] = 1f;
            UploadSsbo(gl, culledCommands, culled);

            // Local position is inside cell 0, but will be transformed into cell 1.
            WriteSurfel(gl, surfelBuffer, 0, new SurfelGpu
            {
                PosRadius = new Vector4(0.1f, 0.1f, 0.1f, 0.1f),
                Normal = new Vector4(0, 0, 1, 0),
                Albedo = new Vector4(1, 0, 0, 1),
                MetaX = 0,
                MetaY = 1,
                MetaZ = 0, // TransformId/commandIndex
                MetaW = 0,
            });

            gl.UseProgram(program);
            SetUniform(gl, program, "maxSurfels", maxSurfels);
            SetUniform(gl, program, "hasCulledCommands", true);
            SetUniform(gl, program, "culledFloatCount", 48u);
            SetUniform(gl, program, "culledCommandFloats", 48u);
            SetUniform(gl, program, "gridOrigin", Vector3.Zero);
            SetUniform(gl, program, "cellSize", 1f);
            SetUniformUVec3(gl, program, "gridDim", gridX, gridY, gridZ);
            SetUniform(gl, program, "maxPerCell", maxPerCell);

            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // Expect the transformed world position to land in cell 1, not cell 0.
            ReadUIntFromSsbo(gl, gridCounts, 0).ShouldBe(0u);
            ReadUIntFromSsbo(gl, gridCounts, 1).ShouldBe(1u);
            ReadUIntFromSsbo(gl, gridIndices, maxPerCell).ShouldBe(0u);

            gl.DeleteBuffer(culledCommands);
            gl.DeleteBuffer(surfelBuffer);
            gl.DeleteBuffer(gridCounts);
            gl.DeleteBuffer(gridIndices);
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
    public unsafe void SurfelGI_Recycle_Dispatch_DeactivatesOldSurfelsAndPushesFreeId()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        const uint maxSurfels = 64u;
        const uint oldId = 5u;
        const uint frameIndex = 100u;
        const uint maxAgeFrames = 10u;

        try
        {
            string initPath = ShaderPath("Compute", "SurfelGI", "Init.comp");
            string recyclePath = ShaderPath("Compute", "SurfelGI", "Recycle.comp");
            InconclusiveIfMissing(initPath);
            InconclusiveIfMissing(recyclePath);

            uint initShader = CompileComputeShader(gl, File.ReadAllText(initPath));
            uint initProgram = CreateComputeProgram(gl, initShader);
            uint recycleShader = CompileComputeShader(gl, File.ReadAllText(recyclePath));
            uint recycleProgram = CreateComputeProgram(gl, recycleShader);

            uint surfelBuffer = CreateSsbo(gl, (nuint)(maxSurfels * (uint)Marshal.SizeOf<SurfelGpu>()), BufferUsageARB.DynamicDraw, 0);
            uint counterBuffer = CreateSsbo(gl, 16, BufferUsageARB.DynamicDraw, 1);
            uint freeStackBuffer = CreateSsbo(gl, (nuint)(maxSurfels * sizeof(uint)), BufferUsageARB.DynamicDraw, 2);

            // Init sets stackTop=maxSurfels and freeIds[i]=i.
            gl.UseProgram(initProgram);
            SetUniform(gl, initProgram, "maxSurfels", maxSurfels);
            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // Simulate one allocation so recycle can push back.
            WriteIntToSsbo(gl, counterBuffer, (int)maxSurfels - 1);
            ReadIntFromSsbo(gl, counterBuffer).ShouldBe((int)maxSurfels - 1);

            // Mark one surfel active and very old.
            WriteSurfel(gl, surfelBuffer, oldId, new SurfelGpu
            {
                PosRadius = new Vector4(0, 0, 0, 0.1f),
                Normal = new Vector4(0, 0, 1, 0),
                Albedo = new Vector4(1, 1, 1, 1),
                MetaX = 0u,
                MetaY = 1u,
                MetaZ = 0u,
                MetaW = 0u,
            });

            gl.UseProgram(recycleProgram);
            SetUniform(gl, recycleProgram, "maxSurfels", maxSurfels);
            SetUniform(gl, recycleProgram, "frameIndex", frameIndex);
            SetUniform(gl, recycleProgram, "maxAgeFrames", maxAgeFrames);
            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // Surfel should be deactivated.
            ReadSurfel(gl, surfelBuffer, oldId).MetaY.ShouldBe(0u);

            // stackTop should have incremented back.
            ReadIntFromSsbo(gl, counterBuffer).ShouldBe((int)maxSurfels);

            // Freed id should be pushed at the previous top (maxSurfels-1).
            ReadUIntFromSsbo(gl, freeStackBuffer, maxSurfels - 1u).ShouldBe(oldId);

            gl.DeleteBuffer(surfelBuffer);
            gl.DeleteBuffer(counterBuffer);
            gl.DeleteBuffer(freeStackBuffer);
            gl.DeleteProgram(initProgram);
            gl.DeleteShader(initShader);
            gl.DeleteProgram(recycleProgram);
            gl.DeleteShader(recycleShader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public unsafe void SurfelGI_Spawn_Dispatch_AllocatesSurfelAndUpdatesGrid()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        const uint maxSurfels = 64u;
        const int res = 16;
        const uint gridX = 4u;
        const uint gridY = 4u;
        const uint gridZ = 4u;
        const uint maxPerCell = 4u;
        const uint cellCount = gridX * gridY * gridZ;

        try
        {
            string initPath = ShaderPath("Compute", "SurfelGI", "Init.comp");
            string spawnPath = ShaderPath("Compute", "SurfelGI", "Spawn.comp");
            InconclusiveIfMissing(initPath);
            InconclusiveIfMissing(spawnPath);

            uint initShader = CompileComputeShader(gl, File.ReadAllText(initPath));
            uint initProgram = CreateComputeProgram(gl, initShader);
            uint spawnShader = CompileComputeShader(gl, File.ReadAllText(spawnPath));
            uint spawnProgram = CreateComputeProgram(gl, spawnShader);

            uint surfelBuffer = CreateSsbo(gl, (nuint)(maxSurfels * (uint)Marshal.SizeOf<SurfelGpu>()), BufferUsageARB.DynamicDraw, 0);
            uint counterBuffer = CreateSsbo(gl, 16, BufferUsageARB.DynamicDraw, 1);
            uint freeStackBuffer = CreateSsbo(gl, (nuint)(maxSurfels * sizeof(uint)), BufferUsageARB.DynamicDraw, 2);
            uint gridCounts = CreateSsbo(gl, (nuint)(cellCount * sizeof(uint)), BufferUsageARB.DynamicDraw, 3);
            uint gridIndices = CreateSsbo(gl, (nuint)(cellCount * maxPerCell * sizeof(uint)), BufferUsageARB.DynamicDraw, 4);

            UploadSsbo(gl, gridCounts, new uint[cellCount]);
            UploadSsbo(gl, gridIndices, new uint[cellCount * maxPerCell]);

            // Init stack.
            gl.UseProgram(initProgram);
            SetUniform(gl, initProgram, "maxSurfels", maxSurfels);
            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // Textures (fill everything so the chosen pseudo-random pixel is deterministic).
            uint depthTex = CreateDepthTexture2D(gl, res, res, 0.5f);
            uint normalTex = CreateFloatTexture2D(gl, res, res, new Vector4(0.5f, 0.5f, 1.0f, 1.0f));
            uint albedoTex = CreateFloatTexture2D(gl, res, res, new Vector4(1.0f, 0.0f, 0.0f, 1.0f));
            uint transformIdTex = CreateUIntTexture2D(gl, res, res, 0u);

            BindTextureUnit(gl, depthTex, 0);
            BindTextureUnit(gl, normalTex, 1);
            BindTextureUnit(gl, albedoTex, 2);
            BindTextureUnit(gl, transformIdTex, 3);

            gl.UseProgram(spawnProgram);
            SetUniform(gl, spawnProgram, "hasCulledCommands", false);
            SetUniform(gl, spawnProgram, "culledFloatCount", 0u);
            SetUniform(gl, spawnProgram, "culledCommandFloats", 48u);
            SetUniformIVec2(gl, spawnProgram, "resolution", res, res);
            SetUniformMat4(gl, spawnProgram, "invProjMatrix", Matrix4x4.Identity);
            SetUniformMat4(gl, spawnProgram, "cameraToWorldMatrix", Matrix4x4.Identity);
            SetUniform(gl, spawnProgram, "frameIndex", 1u);
            SetUniform(gl, spawnProgram, "maxSurfels", maxSurfels);

            // Choose grid parameters that guarantee every reconstructed worldPos maps to cell 0.
            SetUniform(gl, spawnProgram, "gridOrigin", new Vector3(-10f, -10f, -10f));
            SetUniform(gl, spawnProgram, "cellSize", 100f);
            SetUniformUVec3(gl, spawnProgram, "gridDim", gridX, gridY, gridZ);
            SetUniform(gl, spawnProgram, "maxPerCell", maxPerCell);

            // One tile (16x16).
            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // One surfel should have been allocated.
            ReadIntFromSsbo(gl, counterBuffer).ShouldBe((int)maxSurfels - 1);
            ReadUIntFromSsbo(gl, gridCounts, 0).ShouldBe(1u);

            uint allocatedId = ReadUIntFromSsbo(gl, gridIndices, 0);
            allocatedId.ShouldBeLessThan(maxSurfels);
            ReadSurfel(gl, surfelBuffer, allocatedId).MetaY.ShouldBe(1u);

            gl.DeleteTexture(depthTex);
            gl.DeleteTexture(normalTex);
            gl.DeleteTexture(albedoTex);
            gl.DeleteTexture(transformIdTex);

            gl.DeleteBuffer(surfelBuffer);
            gl.DeleteBuffer(counterBuffer);
            gl.DeleteBuffer(freeStackBuffer);
            gl.DeleteBuffer(gridCounts);
            gl.DeleteBuffer(gridIndices);
            gl.DeleteProgram(initProgram);
            gl.DeleteShader(initShader);
            gl.DeleteProgram(spawnProgram);
            gl.DeleteShader(spawnShader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public unsafe void SurfelGI_Spawn_Dispatch_WhenHasCulledCommands_StoresObjectSpaceButBinsByWorldSpace()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        const uint maxSurfels = 64u;
        const int res = 16;
        const uint gridX = 4u;
        const uint gridY = 4u;
        const uint gridZ = 4u;
        const uint maxPerCell = 4u;
        const uint cellCount = gridX * gridY * gridZ;
        const uint frameIndex = 7u;
        const float tx = 1.0f;

        static uint Hash(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7feb352du;
                x ^= x >> 15;
                x *= 0x846ca68bu;
                x ^= x >> 16;
                return x;
            }
        }

        try
        {
            string initPath = ShaderPath("Compute", "SurfelGI", "Init.comp");
            string spawnPath = ShaderPath("Compute", "SurfelGI", "Spawn.comp");
            InconclusiveIfMissing(initPath);
            InconclusiveIfMissing(spawnPath);

            uint initShader = CompileComputeShader(gl, File.ReadAllText(initPath));
            uint initProgram = CreateComputeProgram(gl, initShader);
            uint spawnShader = CompileComputeShader(gl, File.ReadAllText(spawnPath));
            uint spawnProgram = CreateComputeProgram(gl, spawnShader);

            uint surfelBuffer = CreateSsbo(gl, (nuint)(maxSurfels * (uint)Marshal.SizeOf<SurfelGpu>()), BufferUsageARB.DynamicDraw, 0);
            uint counterBuffer = CreateSsbo(gl, 16, BufferUsageARB.DynamicDraw, 1);
            uint freeStackBuffer = CreateSsbo(gl, (nuint)(maxSurfels * sizeof(uint)), BufferUsageARB.DynamicDraw, 2);
            uint gridCounts = CreateSsbo(gl, (nuint)(cellCount * sizeof(uint)), BufferUsageARB.DynamicDraw, 3);
            uint gridIndices = CreateSsbo(gl, (nuint)(cellCount * maxPerCell * sizeof(uint)), BufferUsageARB.DynamicDraw, 4);
            uint culledCommands = CreateSsbo(gl, (nuint)(48u * sizeof(float)), BufferUsageARB.DynamicDraw, 5);

            UploadSsbo(gl, surfelBuffer, new SurfelGpu[(int)maxSurfels]);
            UploadSsbo(gl, gridCounts, new uint[cellCount]);
            UploadSsbo(gl, gridIndices, new uint[cellCount * maxPerCell]);

            // Provide a single model matrix (row-major) translating +X by 1.
            // Spawn should store localPos = inverse(model) * worldPos.
            var culled = new float[48];
            culled[0] = 1f; culled[1] = 0f; culled[2] = 0f; culled[3] = tx;
            culled[4] = 0f; culled[5] = 1f; culled[6] = 0f; culled[7] = 0f;
            culled[8] = 0f; culled[9] = 0f; culled[10] = 1f; culled[11] = 0f;
            culled[12] = 0f; culled[13] = 0f; culled[14] = 0f; culled[15] = 1f;
            UploadSsbo(gl, culledCommands, culled);

            // Init stack.
            gl.UseProgram(initProgram);
            SetUniform(gl, initProgram, "maxSurfels", maxSurfels);
            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // Uniform textures (depth>0 to ensure allocation path is reached).
            uint depthTex = CreateDepthTexture2D(gl, res, res, 0.5f);
            uint normalTex = CreateFloatTexture2D(gl, res, res, new Vector4(0.5f, 0.5f, 1.0f, 1.0f));
            uint albedoTex = CreateFloatTexture2D(gl, res, res, new Vector4(1.0f, 0.0f, 0.0f, 1.0f));
            uint transformIdTex = CreateUIntTexture2D(gl, res, res, 0u);

            BindTextureUnit(gl, depthTex, 0);
            BindTextureUnit(gl, normalTex, 1);
            BindTextureUnit(gl, albedoTex, 2);
            BindTextureUnit(gl, transformIdTex, 3);

            // Choose grid parameters that include the reconstructed world position range.
            var gridOrigin = new Vector3(-2f, -2f, -2f);
            const float cellSize = 1f;

            gl.UseProgram(spawnProgram);
            SetUniform(gl, spawnProgram, "hasCulledCommands", true);
            SetUniform(gl, spawnProgram, "culledFloatCount", 48u);
            SetUniform(gl, spawnProgram, "culledCommandFloats", 48u);
            SetUniformIVec2(gl, spawnProgram, "resolution", res, res);
            SetUniformMat4(gl, spawnProgram, "invProjMatrix", Matrix4x4.Identity);
            SetUniformMat4(gl, spawnProgram, "cameraToWorldMatrix", Matrix4x4.Identity);
            SetUniform(gl, spawnProgram, "frameIndex", frameIndex);
            SetUniform(gl, spawnProgram, "maxSurfels", maxSurfels);
            SetUniform(gl, spawnProgram, "gridOrigin", gridOrigin);
            SetUniform(gl, spawnProgram, "cellSize", cellSize);
            SetUniformUVec3(gl, spawnProgram, "gridDim", gridX, gridY, gridZ);
            SetUniform(gl, spawnProgram, "maxPerCell", maxPerCell);

            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // Determine which pixel Spawn chose (tile=(0,0) for this dispatch).
            uint h = Hash(unchecked(frameIndex * 83492791u));
            uint ox = h & 15u;
            uint oy = (h >> 4) & 15u;
            uint px = Math.Min(ox, (uint)(res - 1));
            uint py = Math.Min(oy, (uint)(res - 1));
            float uvx = ((float)px + 0.5f) / res;
            float uvy = ((float)py + 0.5f) / res;

            // With identity matrices and depth=0.5, worldPos is clip space: (uv*2-1, 0).
            var worldPos = new Vector3(uvx * 2f - 1f, uvy * 2f - 1f, 0f);
            var expectedLocal = worldPos - new Vector3(tx, 0f, 0f);

            int cx = (int)MathF.Floor((worldPos.X - gridOrigin.X) / cellSize);
            int cy = (int)MathF.Floor((worldPos.Y - gridOrigin.Y) / cellSize);
            int cz = (int)MathF.Floor((worldPos.Z - gridOrigin.Z) / cellSize);
            cx.ShouldBeGreaterThanOrEqualTo(0);
            cy.ShouldBeGreaterThanOrEqualTo(0);
            cz.ShouldBeGreaterThanOrEqualTo(0);
            cx.ShouldBeLessThan((int)gridX);
            cy.ShouldBeLessThan((int)gridY);
            cz.ShouldBeLessThan((int)gridZ);

            uint cell = (uint)cx + gridX * ((uint)cy + gridY * (uint)cz);
            ReadUIntFromSsbo(gl, gridCounts, cell).ShouldBe(1u);

            uint allocatedId = ReadUIntFromSsbo(gl, gridIndices, (nuint)(cell * maxPerCell));
            allocatedId.ShouldBeLessThan(maxSurfels);

            var s = ReadSurfel(gl, surfelBuffer, allocatedId);
            s.MetaY.ShouldBe(1u);
            s.MetaX.ShouldBe(frameIndex);
            s.MetaZ.ShouldBe(0u);
            MathF.Abs(s.PosRadius.X - expectedLocal.X).ShouldBeLessThan(1e-4f);
            MathF.Abs(s.PosRadius.Y - expectedLocal.Y).ShouldBeLessThan(1e-4f);
            MathF.Abs(s.PosRadius.Z - expectedLocal.Z).ShouldBeLessThan(1e-4f);
            MathF.Abs(s.Normal.X - 0f).ShouldBeLessThan(1e-4f);
            MathF.Abs(s.Normal.Y - 0f).ShouldBeLessThan(1e-4f);
            MathF.Abs(s.Normal.Z - 1f).ShouldBeLessThan(1e-4f);

            gl.DeleteTexture(depthTex);
            gl.DeleteTexture(normalTex);
            gl.DeleteTexture(albedoTex);
            gl.DeleteTexture(transformIdTex);

            gl.DeleteBuffer(culledCommands);
            gl.DeleteBuffer(surfelBuffer);
            gl.DeleteBuffer(counterBuffer);
            gl.DeleteBuffer(freeStackBuffer);
            gl.DeleteBuffer(gridCounts);
            gl.DeleteBuffer(gridIndices);
            gl.DeleteProgram(initProgram);
            gl.DeleteShader(initShader);
            gl.DeleteProgram(spawnProgram);
            gl.DeleteShader(spawnShader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public unsafe void SurfelGI_Spawn_Dispatch_WhenFreeStackEmpty_DoesNotAllocateOrModifyGrid()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        const uint maxSurfels = 64u;
        const int res = 16;
        const uint gridX = 4u;
        const uint gridY = 4u;
        const uint gridZ = 4u;
        const uint maxPerCell = 4u;
        const uint cellCount = gridX * gridY * gridZ;

        try
        {
            string spawnPath = ShaderPath("Compute", "SurfelGI", "Spawn.comp");
            InconclusiveIfMissing(spawnPath);

            uint spawnShader = CompileComputeShader(gl, File.ReadAllText(spawnPath));
            uint spawnProgram = CreateComputeProgram(gl, spawnShader);

            uint surfelBuffer = CreateSsbo(gl, (nuint)(maxSurfels * (uint)Marshal.SizeOf<SurfelGpu>()), BufferUsageARB.DynamicDraw, 0);
            uint counterBuffer = CreateSsbo(gl, 16, BufferUsageARB.DynamicDraw, 1);
            uint freeStackBuffer = CreateSsbo(gl, (nuint)(maxSurfels * sizeof(uint)), BufferUsageARB.DynamicDraw, 2);
            uint gridCounts = CreateSsbo(gl, (nuint)(cellCount * sizeof(uint)), BufferUsageARB.DynamicDraw, 3);
            uint gridIndices = CreateSsbo(gl, (nuint)(cellCount * maxPerCell * sizeof(uint)), BufferUsageARB.DynamicDraw, 4);

            // Deterministic initialization.
            UploadSsbo(gl, surfelBuffer, new SurfelGpu[(int)maxSurfels]);
            UploadSsbo(gl, gridCounts, new uint[cellCount]);
            UploadSsbo(gl, gridIndices, new uint[cellCount * maxPerCell]);
            UploadSsbo(gl, freeStackBuffer, new uint[maxSurfels]);

            // Empty free stack: Spawn will atomicAdd(stackTop, -1) once and early-out.
            WriteIntToSsbo(gl, counterBuffer, 0);

            // Textures (ensure depth > 0 so we reach the allocation code path).
            uint depthTex = CreateDepthTexture2D(gl, res, res, 0.5f);
            uint normalTex = CreateFloatTexture2D(gl, res, res, new Vector4(0.5f, 0.5f, 1.0f, 1.0f));
            uint albedoTex = CreateFloatTexture2D(gl, res, res, new Vector4(1.0f, 0.0f, 0.0f, 1.0f));
            uint transformIdTex = CreateUIntTexture2D(gl, res, res, 0u);

            BindTextureUnit(gl, depthTex, 0);
            BindTextureUnit(gl, normalTex, 1);
            BindTextureUnit(gl, albedoTex, 2);
            BindTextureUnit(gl, transformIdTex, 3);

            gl.UseProgram(spawnProgram);
            SetUniform(gl, spawnProgram, "hasCulledCommands", false);
            SetUniform(gl, spawnProgram, "culledFloatCount", 0u);
            SetUniform(gl, spawnProgram, "culledCommandFloats", 48u);
            SetUniformIVec2(gl, spawnProgram, "resolution", res, res);
            SetUniformMat4(gl, spawnProgram, "invProjMatrix", Matrix4x4.Identity);
            SetUniformMat4(gl, spawnProgram, "cameraToWorldMatrix", Matrix4x4.Identity);
            SetUniform(gl, spawnProgram, "frameIndex", 1u);
            SetUniform(gl, spawnProgram, "maxSurfels", maxSurfels);
            SetUniform(gl, spawnProgram, "gridOrigin", new Vector3(-10f, -10f, -10f));
            SetUniform(gl, spawnProgram, "cellSize", 100f);
            SetUniformUVec3(gl, spawnProgram, "gridDim", gridX, gridY, gridZ);
            SetUniform(gl, spawnProgram, "maxPerCell", maxPerCell);

            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // With empty stack, Spawn should not allocate or touch the grid.
            ReadUIntFromSsbo(gl, gridCounts, 0).ShouldBe(0u);
            ReadSurfel(gl, surfelBuffer, 0).MetaY.ShouldBe(0u);

            // Note: Spawn uses atomicAdd(stackTop, -1) and returns if it goes negative.
            ReadIntFromSsbo(gl, counterBuffer).ShouldBe(-1);

            gl.DeleteTexture(depthTex);
            gl.DeleteTexture(normalTex);
            gl.DeleteTexture(albedoTex);
            gl.DeleteTexture(transformIdTex);

            gl.DeleteBuffer(surfelBuffer);
            gl.DeleteBuffer(counterBuffer);
            gl.DeleteBuffer(freeStackBuffer);
            gl.DeleteBuffer(gridCounts);
            gl.DeleteBuffer(gridIndices);
            gl.DeleteProgram(spawnProgram);
            gl.DeleteShader(spawnShader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public unsafe void SurfelGI_Shade_Dispatch_UpdatesSurfelLastUsedFrame()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        const uint maxSurfels = 16u;
        const uint surfelId = 0u;
        const uint frameIndex = 123u;
        const int res = 16;
        const uint gridX = 4u;
        const uint gridY = 4u;
        const uint gridZ = 4u;
        const uint maxPerCell = 4u;
        const uint cellCount = gridX * gridY * gridZ;

        try
        {
            string shaderPath = ShaderPath("Compute", "SurfelGI", "Shade.comp");
            InconclusiveIfMissing(shaderPath);

            uint shader = CompileComputeShader(gl, File.ReadAllText(shaderPath));
            uint program = CreateComputeProgram(gl, shader);

            uint surfelBuffer = CreateSsbo(gl, (nuint)(maxSurfels * (uint)Marshal.SizeOf<SurfelGpu>()), BufferUsageARB.DynamicDraw, 0);
            uint counterBuffer = CreateSsbo(gl, 16, BufferUsageARB.DynamicDraw, 1);
            uint gridCounts = CreateSsbo(gl, (nuint)(cellCount * sizeof(uint)), BufferUsageARB.DynamicDraw, 3);
            uint gridIndices = CreateSsbo(gl, (nuint)(cellCount * maxPerCell * sizeof(uint)), BufferUsageARB.DynamicDraw, 4);

            // Put one active surfel in cell 0.
            UploadSsbo(gl, gridCounts, new uint[cellCount]);
            UploadSsbo(gl, gridIndices, new uint[cellCount * maxPerCell]);

            // counts[0]=1, indices[0]=surfelId
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, gridCounts);
            uint* countsPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadWrite);
            countsPtr[0] = 1u;
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);

            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, gridIndices);
            uint* idxPtr = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadWrite);
            idxPtr[0] = surfelId;
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);

            WriteSurfel(gl, surfelBuffer, surfelId, new SurfelGpu
            {
                PosRadius = new Vector4(0f, 0f, 1f, 0.5f),
                Normal = new Vector4(0f, 0f, -1f, 0f),
                Albedo = new Vector4(1f, 0f, 0f, 1f),
                MetaX = 0u,
                MetaY = 1u,
                MetaZ = 0u,
                MetaW = 0u,
            });

            // Textures.
            uint depthTex = CreateDepthTexture2D(gl, res, res, 0.5f);
            uint normalTex = CreateFloatTexture2D(gl, res, res, new Vector4(0.5f, 0.5f, 1.0f, 1.0f));
            uint albedoTex = CreateFloatTexture2D(gl, res, res, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
            uint outTex = CreateOutputTexture2D(gl, res, res);

            BindTextureUnit(gl, depthTex, 0);
            BindTextureUnit(gl, normalTex, 1);
            BindTextureUnit(gl, albedoTex, 2);
            gl.BindImageTexture(3, outTex, 0, false, 0, BufferAccessARB.WriteOnly, InternalFormat.Rgba16f);

            gl.UseProgram(program);
            SetUniform(gl, program, "hasCulledCommands", false);
            SetUniform(gl, program, "culledFloatCount", 0u);
            SetUniform(gl, program, "culledCommandFloats", 48u);
            SetUniformIVec2(gl, program, "resolution", res, res);
            SetUniformMat4(gl, program, "invProjMatrix", Matrix4x4.Identity);
            SetUniformMat4(gl, program, "cameraToWorldMatrix", Matrix4x4.Identity);
            SetUniform(gl, program, "frameIndex", frameIndex);
            SetUniform(gl, program, "maxSurfels", maxSurfels);
            SetUniform(gl, program, "gridOrigin", new Vector3(-10f, -10f, -10f));
            SetUniform(gl, program, "cellSize", 100f);
            SetUniformUVec3(gl, program, "gridDim", gridX, gridY, gridZ);
            SetUniform(gl, program, "maxPerCell", maxPerCell);

            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.ShaderImageAccessBarrierBit);

            // Shade should have weighted the surfel and updated meta.x via atomicMax.
            ReadSurfel(gl, surfelBuffer, surfelId).MetaX.ShouldBe(frameIndex);

            gl.DeleteTexture(depthTex);
            gl.DeleteTexture(normalTex);
            gl.DeleteTexture(albedoTex);
            gl.DeleteTexture(outTex);

            gl.DeleteBuffer(surfelBuffer);
            gl.DeleteBuffer(counterBuffer);
            gl.DeleteBuffer(gridCounts);
            gl.DeleteBuffer(gridIndices);
            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }
}
