using NUnit.Framework;
using Shouldly;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Assert = NUnit.Framework.Assert;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class IndirectMultiDrawTests
{
    private const int Width = 256;
    private const int Height = 256;
    private static readonly Vector3 LeftCubeOffset = new(-1.2f, 0f, 0f);
    private static readonly Vector3 RightCubeOffset = new(1.2f, 0f, 0f);

    private static bool IsTrue(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return false;

        v = v.Trim();
        return
            v.Equals("1") ||
            v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    // Defaults: show window and block until close
    private static bool ShowWindow
    {
        get
        {
            bool hide = IsTrue(Environment.GetEnvironmentVariable("XR_HIDE_TEST_WINDOWS")) ||
                        IsTrue(NUnit.Framework.TestContext.Parameters.Get("HideWindow", "false"));
            if (hide) return false;

            // default true unless explicitly disabled
            bool showParam = IsTrue(NUnit.Framework.TestContext.Parameters.Get("ShowWindow", "true"));
            bool showEnv = IsTrue(Environment.GetEnvironmentVariable("XR_SHOW_TEST_WINDOWS")) ||
                           IsTrue(Environment.GetEnvironmentVariable("XR_SHOW_GL_TEST"));
            return showParam || showEnv;
        }
    }

    private static bool ShowWindowBlock
    {
        get
        {
            bool noBlock = IsTrue(Environment.GetEnvironmentVariable("XR_SHOW_TEST_NO_BLOCK")) ||
                           IsTrue(NUnit.Framework.TestContext.Parameters.Get("ShowWindowNoBlock", "false"));
            if (noBlock) return false;

            // default true unless explicitly disabled
            bool blockParam = IsTrue(NUnit.Framework.TestContext.Parameters.Get("ShowWindowBlock", "true"));
            bool blockEnv = IsTrue(Environment.GetEnvironmentVariable("XR_SHOW_TEST_BLOCK"));
            return blockParam || blockEnv;
        }
    }

    private static int ShowWindowDurationMs
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("XR_SHOW_TEST_WINDOW_MS")
                      ?? Environment.GetEnvironmentVariable("XR_SHOW_GL_TEST_MS");
            var param = NUnit.Framework.TestContext.Parameters.Get("ShowWindowMs", "1000");
            return int.TryParse(env, out var msEnv) ? msEnv : (int.TryParse(param, out var msParam) ? msParam : 1000);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DrawElementsIndirectCommand
    {
        public uint Count;
        public uint InstanceCount;
        public uint FirstIndex;
        public int BaseVertex;
        public uint BaseInstance;
    }

    [Test]
    public unsafe void MultiDrawElementsIndirect_RendersTwoDistinctCubes()
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(Width, Height);
        options.IsVisible = ShowWindow;
        options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));

        using var window = Window.Create(options);

        try
        {
            window.Initialize();
            window.MakeCurrent();
            window.DoEvents();
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"Failed to initialize OpenGL context: {ex.Message}");
            return;
        }

        var gl = GL.GetApi(window);

        uint vao = gl.GenVertexArray();
        uint vbo = gl.GenBuffer();
        uint ebo = gl.GenBuffer();
        uint indirectBuffer = gl.GenBuffer();

        gl.BindVertexArray(vao);

        float[] vertices = BuildVertexData();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices.AsSpan(), BufferUsageARB.StaticDraw);

        uint[] indices = BuildIndexData();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, indices.AsSpan(), BufferUsageARB.StaticDraw);

        DrawElementsIndirectCommand[] commands = BuildCommands();
        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, indirectBuffer);
        gl.BufferData<DrawElementsIndirectCommand>(BufferTargetARB.DrawIndirectBuffer, commands.AsSpan(), BufferUsageARB.StaticDraw);

        const int stride = 6 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));

        uint vertexShader = CompileShader(gl, ShaderType.VertexShader, VertexShaderSource);
        uint fragmentShader = CompileShader(gl, ShaderType.FragmentShader, FragmentShaderSource);
        uint program = LinkProgram(gl, vertexShader, fragmentShader);

        gl.UseProgram(program);

        int mvpLocation = gl.GetUniformLocation(program, "uMVP");
        Matrix4x4 mvp = BuildMvpMatrix();
        Span<float> mvpSpan = stackalloc float[16];
        CopyMatrix(mvp, mvpSpan);
        // Transpose so GLSL column-major consumes our row-major matrix correctly
        gl.UniformMatrix4(mvpLocation, 1, true, mvpSpan);

        gl.Viewport(0, 0, (uint)Width, (uint)Height);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Less);

        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, indirectBuffer);
        uint commandStride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
        gl.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, (void*)0, 2, commandStride);
        gl.Finish();

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        gl.ReadBuffer(ReadBufferMode.Back);

        var leftPixelPos = ProjectToPixel(mvp, LeftCubeOffset);
        var rightPixelPos = ProjectToPixel(mvp, RightCubeOffset);

        var leftPixel = ReadPixel(gl, leftPixelPos.X, leftPixelPos.Y);
        var rightPixel = ReadPixel(gl, rightPixelPos.X, rightPixelPos.Y);

        leftPixel.Red.ShouldBeGreaterThan((byte)128, "Left cube should contribute red channel");
        leftPixel.Green.ShouldBeLessThan((byte)64, "Left cube should have minimal green channel");

        rightPixel.Green.ShouldBeGreaterThan((byte)128, "Right cube should contribute green channel");
        rightPixel.Red.ShouldBeLessThan((byte)64, "Right cube should have minimal red channel");

        if (ShowWindow)
        {
            window.SwapBuffers();
            if (ShowWindowBlock)
            {
                while (!window.IsClosing)
                {
                    window.DoEvents();
                    Thread.Sleep(16);
                }
            }
            else
            {
                var end = DateTime.UtcNow.AddMilliseconds(ShowWindowDurationMs);
                while (DateTime.UtcNow < end && !window.IsClosing)
                {
                    window.DoEvents();
                    Thread.Sleep(16);
                }
            }
        }

        gl.DisableVertexAttribArray(0);
        gl.DisableVertexAttribArray(1);

        gl.UseProgram(0);
        gl.DeleteProgram(program);
        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

        gl.DeleteBuffer(indirectBuffer);
        gl.DeleteBuffer(ebo);
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);

        window.Close();
    }

    [Test]
    public unsafe void MultiDrawElementsIndirectCount_RendersTwoDistinctCubes_UsingGpuCount()
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(Width, Height);
        options.IsVisible = ShowWindow;
        options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));

        using var window = Window.Create(options);

        try
        {
            window.Initialize();
            window.MakeCurrent();
            window.DoEvents();
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"Failed to initialize OpenGL context: {ex.Message}");
            return;
        }

        var gl = GL.GetApi(window);

        uint vao = gl.GenVertexArray();
        uint vbo = gl.GenBuffer();
        uint ebo = gl.GenBuffer();
        uint indirectBuffer = gl.GenBuffer();
        uint parameterBuffer = gl.GenBuffer();

        gl.BindVertexArray(vao);

        float[] vertices = BuildVertexData();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices.AsSpan(), BufferUsageARB.StaticDraw);

        uint[] indices = BuildIndexData();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, indices.AsSpan(), BufferUsageARB.StaticDraw);

        DrawElementsIndirectCommand[] commands = BuildCommands();
        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, indirectBuffer);
        gl.BufferData<DrawElementsIndirectCommand>(BufferTargetARB.DrawIndirectBuffer, commands.AsSpan(), BufferUsageARB.StaticDraw);

        // GPU-specified count: put value 2 into parameter buffer at offset 0
        uint[] drawCountData = [2u];
        gl.BindBuffer(GLEnum.ParameterBuffer, parameterBuffer);
        gl.BufferData<uint>(GLEnum.ParameterBuffer, drawCountData.AsSpan(), BufferUsageARB.DynamicDraw);

        const int stride = 6 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));

        uint vertexShader = CompileShader(gl, ShaderType.VertexShader, VertexShaderSource);
        uint fragmentShader = CompileShader(gl, ShaderType.FragmentShader, FragmentShaderSource);
        uint program = LinkProgram(gl, vertexShader, fragmentShader);

        gl.UseProgram(program);

        int mvpLocation = gl.GetUniformLocation(program, "uMVP");
        Matrix4x4 mvp = BuildMvpMatrix();
        Span<float> mvpSpan = stackalloc float[16];
        CopyMatrix(mvp, mvpSpan);
        gl.UniformMatrix4(mvpLocation, 1, true, mvpSpan);

        gl.Viewport(0, 0, (uint)Width, (uint)Height);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Less);

        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, indirectBuffer);
        gl.BindBuffer(GLEnum.ParameterBuffer, parameterBuffer);
        uint commandStride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();

        // drawcount offset = 0 within parameter buffer, maxdrawcount = 2
        gl.MultiDrawElementsIndirectCount(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, (void*)0, (nint)0, 2, commandStride);
        gl.Finish();

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        gl.ReadBuffer(ReadBufferMode.Back);

        var leftPixelPos = ProjectToPixel(mvp, LeftCubeOffset);
        var rightPixelPos = ProjectToPixel(mvp, RightCubeOffset);

        var leftPixel = ReadPixel(gl, leftPixelPos.X, leftPixelPos.Y);
        var rightPixel = ReadPixel(gl, rightPixelPos.X, rightPixelPos.Y);

        leftPixel.Red.ShouldBeGreaterThan((byte)128, "Left cube should contribute red channel");
        leftPixel.Green.ShouldBeLessThan((byte)64, "Left cube should have minimal green channel");

        rightPixel.Green.ShouldBeGreaterThan((byte)128, "Right cube should contribute green channel");
        rightPixel.Red.ShouldBeLessThan((byte)64, "Right cube should have minimal red channel");

        if (ShowWindow)
        {
            window.SwapBuffers();
            if (ShowWindowBlock)
            {
                while (!window.IsClosing)
                {
                    window.DoEvents();
                    Thread.Sleep(16);
                }
            }
            else
            {
                var end = DateTime.UtcNow.AddMilliseconds(ShowWindowDurationMs);
                while (DateTime.UtcNow < end && !window.IsClosing)
                {
                    window.DoEvents();
                    Thread.Sleep(16);
                }
            }
        }

        gl.DisableVertexAttribArray(0);
        gl.DisableVertexAttribArray(1);

        gl.UseProgram(0);
        gl.DeleteProgram(program);
        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
        gl.BindBuffer(GLEnum.ParameterBuffer, 0);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

        gl.DeleteBuffer(parameterBuffer);
        gl.DeleteBuffer(indirectBuffer);
        gl.DeleteBuffer(ebo);
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);

        window.Close();
    }

    private static float[] BuildVertexData()
    {
        const float half = 0.5f;
        Vector3[] basePositions =
        [
            new(-half, -half, -half),
            new(half, -half, -half),
            new(half, half, -half),
            new(-half, half, -half),
            new(-half, -half, half),
            new(half, -half, half),
            new(half, half, half),
            new(-half, half, half)
        ];

        float[] data = new float[16 * 6];
        int write = 0;

        for (int i = 0; i < basePositions.Length; i++)
        {
            Vector3 pos = basePositions[i] + LeftCubeOffset;
            data[write++] = pos.X;
            data[write++] = pos.Y;
            data[write++] = pos.Z;
            data[write++] = 1f;
            data[write++] = 0f;
            data[write++] = 0f;
        }

        for (int i = 0; i < basePositions.Length; i++)
        {
            Vector3 pos = basePositions[i] + RightCubeOffset;
            data[write++] = pos.X;
            data[write++] = pos.Y;
            data[write++] = pos.Z;
            data[write++] = 0f;
            data[write++] = 1f;
            data[write++] = 0f;
        }

        return data;
    }

    private static uint[] BuildIndexData()
    {
        uint[] cube =
        [
            0, 1, 2, 2, 3, 0,
            4, 5, 6, 6, 7, 4,
            0, 4, 7, 7, 3, 0,
            1, 5, 6, 6, 2, 1,
            3, 2, 6, 6, 7, 3,
            0, 1, 5, 5, 4, 0
        ];

        uint[] indices = new uint[72];
        Array.Copy(cube, indices, cube.Length);
        Array.Copy(cube, 0, indices, cube.Length, cube.Length);

        return indices;
    }

    private static DrawElementsIndirectCommand[] BuildCommands() =>
        [
            new()
            {
                Count = 36,
                InstanceCount = 1,
                FirstIndex = 0,
                BaseVertex = 0,
                BaseInstance = 0
            },
            new()
            {
                Count = 36,
                InstanceCount = 1,
                FirstIndex = 36,
                BaseVertex = 8,
                BaseInstance = 0
            }
        ];

    private static Matrix4x4 BuildMvpMatrix()
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, Width / (float)Height, 0.1f, 10f);
        var view = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 4f), Vector3.Zero, Vector3.UnitY);
        return Matrix4x4.Multiply(view, projection);
    }

    private static void CopyMatrix(in Matrix4x4 matrix, Span<float> destination)
    {
        destination[0] = matrix.M11;
        destination[1] = matrix.M12;
        destination[2] = matrix.M13;
        destination[3] = matrix.M14;
        destination[4] = matrix.M21;
        destination[5] = matrix.M22;
        destination[6] = matrix.M23;
        destination[7] = matrix.M24;
        destination[8] = matrix.M31;
        destination[9] = matrix.M32;
        destination[10] = matrix.M33;
        destination[11] = matrix.M34;
        destination[12] = matrix.M41;
        destination[13] = matrix.M42;
        destination[14] = matrix.M43;
        destination[15] = matrix.M44;
    }

    private static (byte Red, byte Green, byte Blue) ReadPixel(GL gl, int x, int y)
    {
        Span<byte> rgba = stackalloc byte[4];
        gl.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        return (rgba[0], rgba[1], rgba[2]);
    }

    private static (int X, int Y) ProjectToPixel(in Matrix4x4 mvp, Vector3 position)
    {
        Vector4 clip = Vector4.Transform(new Vector4(position, 1f), mvp);
        if (MathF.Abs(clip.W) < 1e-6f)
        {
            return (Width / 2, Height / 2);
        }

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;

        float screenX = ((ndcX * 0.5f) + 0.5f) * (Width - 1);
        float screenY = ((ndcY * 0.5f) + 0.5f) * (Height - 1);

        int clampedX = (int)Math.Clamp(screenX, 0f, Width - 1);
        int clampedY = (int)Math.Clamp(screenY, 0f, Width - 1);

        return (clampedX, clampedY);
    }

    private static uint CompileShader(GL gl, ShaderType type, string source)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);

        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
        {
            string infoLog = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"Failed to compile {type}: {infoLog}");
        }

        return shader;
    }

    private static uint LinkProgram(GL gl, uint vertexShader, uint fragmentShader)
    {
        uint program = gl.CreateProgram();
        gl.AttachShader(program, vertexShader);
        gl.AttachShader(program, fragmentShader);
        gl.LinkProgram(program);

        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int status);
        if (status == 0)
        {
            string infoLog = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            throw new InvalidOperationException($"Failed to link program: {infoLog}");
        }

        return program;
    }

    private const string VertexShaderSource = "#version 450 core\n" +
        "layout(location = 0) in vec3 in_position;\n" +
        "layout(location = 1) in vec3 in_color;\n" +
        "uniform mat4 uMVP;\n" +
        "out vec3 vColor;\n" +
        "void main()\n" +
        "{\n" +
        "    gl_Position = uMVP * vec4(in_position, 1.0);\n" +
        "    vColor = in_color;\n" +
        "}";

    private const string FragmentShaderSource = "#version 450 core\n" +
        "in vec3 vColor;\n" +
        "out vec4 fragColor;\n" +
        "void main()\n" +
        "{\n" +
        "    fragColor = vec4(vColor, 1.0);\n" +
        "}";
}
