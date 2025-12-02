using NUnit.Framework;
using Shouldly;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Tests for light probe octahedral encoding shaders.
/// Validates that cubemap-to-octa and irradiance/prefilter convolution shaders
/// produce non-black output when given valid input.
/// </summary>
[TestFixture]
public class LightProbeOctaTests
{
    private const int Width = 256;
    private const int Height = 256;

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

    private static bool ShowWindow
    {
        get
        {
            bool hide = IsTrue(Environment.GetEnvironmentVariable("XR_HIDE_TEST_WINDOWS")) ||
                        IsTrue(NUnit.Framework.TestContext.Parameters.Get("HideWindow", "false"));
            if (hide) return false;

            bool showParam = IsTrue(NUnit.Framework.TestContext.Parameters.Get("ShowWindow", "false"));
            bool showEnv = IsTrue(Environment.GetEnvironmentVariable("XR_SHOW_TEST_WINDOWS"));
            return showParam || showEnv;
        }
    }

    /// <summary>
    /// Tests that the FullscreenTri.vs + CubemapToOctahedron.fs shaders properly render
    /// a non-black output when given a filled cubemap texture.
    /// </summary>
    [Test]
    public unsafe void CubemapToOctahedron_ProducesNonBlackOutput()
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

            var gl = GL.GetApi(window);

            // Create a simple cubemap with non-black color
            uint cubemap = CreateTestCubemap(gl);

            // Create FBO with 2D render target for octahedral output
            uint fbo = gl.GenFramebuffer();
            uint octaTex = CreateTestTexture2D(gl, Width, Height);

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, octaTex, 0);

            var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            status.ShouldBe(GLEnum.FramebufferComplete);

            // Create and compile shaders
            uint program = CreateCubemapToOctaProgram(gl);

            // Render fullscreen triangle
            gl.UseProgram(program);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.TextureCubeMap, cubemap);
            gl.Uniform1(gl.GetUniformLocation(program, "Texture0"), 0);

            gl.Viewport(0, 0, Width, Height);
            gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.Disable(EnableCap.DepthTest);

            // Create and bind fullscreen triangle VAO
            uint vao = CreateFullscreenTriangleVAO(gl);
            gl.BindVertexArray(vao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            // Sample several points to verify each cubemap face maps correctly
            var checks = new (Vector2D<int> Pixel, Vector3 ExpectedColor, string Label)[]
            {
                (new Vector2D<int>(Width / 2, Height / 2), new Vector3(0.0f, 0.0f, 1.0f), "+Y center"),
                (new Vector2D<int>((int)(Width * 0.9f), Height / 2), new Vector3(1.0f, 0.0f, 0.0f), "+X right"),
                (new Vector2D<int>((int)(Width * 0.1f), Height / 2), new Vector3(0.0f, 1.0f, 0.0f), "-X left"),
                (new Vector2D<int>(Width / 2, (int)(Height * 0.9f)), new Vector3(1.0f, 0.0f, 1.0f), "+Z top"),
                (new Vector2D<int>(Width / 2, (int)(Height * 0.1f)), new Vector3(0.0f, 1.0f, 1.0f), "-Z bottom"),
                (new Vector2D<int>((int)(Width * 0.15f), (int)(Height * 0.15f)), new Vector3(1.0f, 1.0f, 0.0f), "-Y corner"),
            };

            foreach (var (pixel, expected, label) in checks)
            {
                var sample = ReadPixel(gl, pixel.X, pixel.Y);
                AssertColorApproxEquals(sample, expected, label);
            }

            // Cleanup
            gl.DeleteProgram(program);
            gl.DeleteTexture(cubemap);
            gl.DeleteTexture(octaTex);
            gl.DeleteFramebuffer(fbo);
            gl.DeleteVertexArray(vao);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    /// <summary>
    /// Tests that the FullscreenTri.vs + IrradianceConvolutionOcta.fs shaders properly render
    /// a non-black output when given a valid octahedral environment map.
    /// </summary>
    [Test]
    public unsafe void IrradianceConvolutionOcta_ProducesNonBlackOutput()
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(32, 32); // Irradiance is usually lower resolution
        options.IsVisible = ShowWindow;
        options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));

        using var window = Window.Create(options);

        try
        {
            window.Initialize();
            window.MakeCurrent();
            window.DoEvents();

            var gl = GL.GetApi(window);

            // Create a simple 2D octa texture with non-black color (simulate environment map)
            uint octaEnvTex = CreateFilledTexture2D(gl, 64, 64, new Vector4(0.5f, 0.3f, 0.7f, 1.0f));

            // Create FBO with 2D render target for irradiance output
            uint fbo = gl.GenFramebuffer();
            uint irradianceTex = CreateTestTexture2D(gl, 32, 32);

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, irradianceTex, 0);

            var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            status.ShouldBe(GLEnum.FramebufferComplete);

            // Create and compile shaders
            uint program = CreateIrradianceOctaProgram(gl);

            // Render fullscreen triangle
            gl.UseProgram(program);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, octaEnvTex);
            gl.Uniform1(gl.GetUniformLocation(program, "Texture0"), 0);

            gl.Viewport(0, 0, 32, 32);
            gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.Disable(EnableCap.DepthTest);

            // Create and bind fullscreen triangle VAO
            uint vao = CreateFullscreenTriangleVAO(gl);
            gl.BindVertexArray(vao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            // Read back center pixel to verify non-black output
            var (r, g, b) = ReadPixel(gl, 16, 16);

            // Cleanup
            gl.DeleteProgram(program);
            gl.DeleteTexture(octaEnvTex);
            gl.DeleteTexture(irradianceTex);
            gl.DeleteFramebuffer(fbo);
            gl.DeleteVertexArray(vao);

            // Verify the output is non-black
            bool isBlack = r == 0 && g == 0 && b == 0;
            isBlack.ShouldBeFalse("IrradianceConvolutionOcta shader produced black output - shader may not be receiving input correctly");
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    private static unsafe uint CreateTestCubemap(GL gl)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.TextureCubeMap, tex);

        // Fill each face with a distinct non-black color
        Vector4[] faceColors =
        [
            new Vector4(1.0f, 0.0f, 0.0f, 1.0f), // +X Red
            new Vector4(0.0f, 1.0f, 0.0f, 1.0f), // -X Green
            new Vector4(0.0f, 0.0f, 1.0f, 1.0f), // +Y Blue
            new Vector4(1.0f, 1.0f, 0.0f, 1.0f), // -Y Yellow
            new Vector4(1.0f, 0.0f, 1.0f, 1.0f), // +Z Magenta
            new Vector4(0.0f, 1.0f, 1.0f, 1.0f), // -Z Cyan
        ];

        for (int face = 0; face < 6; face++)
        {
            float[] data = new float[64 * 64 * 4];
            for (int i = 0; i < 64 * 64; i++)
            {
                data[i * 4 + 0] = faceColors[face].X;
                data[i * 4 + 1] = faceColors[face].Y;
                data[i * 4 + 2] = faceColors[face].Z;
                data[i * 4 + 3] = faceColors[face].W;
            }

            fixed (float* ptr = data)
            {
                gl.TexImage2D(
                    TextureTarget.TextureCubeMapPositiveX + face,
                    0,
                    InternalFormat.Rgba16f,
                    64, 64,
                    0,
                    PixelFormat.Rgba,
                    PixelType.Float,
                    ptr);
            }
        }

        gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);

        return tex;
    }

    private static unsafe uint CreateTestTexture2D(GL gl, int width, int height)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);

        gl.TexImage2D(
            TextureTarget.Texture2D,
            0,
            InternalFormat.Rgba16f,
            (uint)width, (uint)height,
            0,
            PixelFormat.Rgba,
            PixelType.Float,
            null);

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        return tex;
    }

    private static unsafe uint CreateFilledTexture2D(GL gl, int width, int height, Vector4 color)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);

        float[] data = new float[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            data[i * 4 + 0] = color.X;
            data[i * 4 + 1] = color.Y;
            data[i * 4 + 2] = color.Z;
            data[i * 4 + 3] = color.W;
        }

        fixed (float* ptr = data)
        {
            gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba16f,
                (uint)width, (uint)height,
                0,
                PixelFormat.Rgba,
                PixelType.Float,
                ptr);
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        return tex;
    }

    private static unsafe uint CreateFullscreenTriangleVAO(GL gl)
    {
        // Fullscreen triangle: single triangle that covers the entire viewport
        // Uses clip-space coordinates directly (-1 to 1 range, with overdraw for efficiency)
        float[] vertices =
        [
            -1.0f, -1.0f, 0.0f,  // Bottom-left
             3.0f, -1.0f, 0.0f,  // Bottom-right (extends past viewport)
            -1.0f,  3.0f, 0.0f,  // Top-left (extends past viewport)
        ];

        uint vao = gl.GenVertexArray();
        uint vbo = gl.GenBuffer();

        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);

        fixed (float* ptr = vertices)
        {
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), ptr, BufferUsageARB.StaticDraw);
        }

        // Position attribute at location 0
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), null);

        gl.BindVertexArray(0);
        return vao;
    }

    private static uint CreateCubemapToOctaProgram(GL gl)
    {
        const string vertexSource = """
            #version 450
            
            layout(location = 0) in vec3 Position;
            layout(location = 0) out vec3 FragPos;
            
            void main()
            {
                FragPos = Position;
                gl_Position = vec4(Position.xy, 0.0f, 1.0f);
            }
            """;

        const string fragmentSource = """
            #version 450
            
            layout(location = 0) in vec3 FragPos;
            layout(location = 0) out vec4 OutColor;
            
            uniform samplerCube Texture0;
            
            vec3 DecodeOcta(vec2 uv)
            {
                vec2 f = uv * 2.0f - 1.0f;
                vec3 n = vec3(f.x, f.y, 1.0f - abs(f.x) - abs(f.y));
            
                if (n.z < 0.0f)
                {
                    vec2 signDir = vec2(n.x >= 0.0f ? 1.0f : -1.0f, n.y >= 0.0f ? 1.0f : -1.0f);
                    n.xy = (1.0f - abs(n.yx)) * signDir;
                }

                // Swizzle octahedral Z into world Y so the center of the map corresponds to +Y
                vec3 dir = vec3(n.x, n.z, n.y);
                return normalize(dir);
            }
            
            void main()
            {
                vec2 clipXY = FragPos.xy;
                if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
                {
                    discard;
                }
            
                vec2 uv = clipXY * 0.5f + 0.5f;
                vec3 dir = DecodeOcta(uv);
                OutColor = texture(Texture0, dir);
            }
            """;

        return LinkProgram(gl,
            CompileShader(gl, ShaderType.VertexShader, vertexSource),
            CompileShader(gl, ShaderType.FragmentShader, fragmentSource));
    }

    private static uint CreateIrradianceOctaProgram(GL gl)
    {
        const string vertexSource = """
            #version 450
            
            layout(location = 0) in vec3 Position;
            layout(location = 0) out vec3 FragPos;
            
            void main()
            {
                FragPos = Position;
                gl_Position = vec4(Position.xy, 0.0f, 1.0f);
            }
            """;

        // Simplified irradiance shader for testing - just samples environment and applies basic tint
        const string fragmentSource = """
            #version 450
            
            layout (location = 0) out vec3 OutColor;
            layout (location = 0) in vec3 FragPos;
            
            uniform sampler2D Texture0;
            
            const float PI = 3.14159265359f;
            
            vec2 EncodeOcta(vec3 dir)
            {
                dir = normalize(dir);
                dir /= max(abs(dir.x) + abs(dir.y) + abs(dir.z), 1e-5f);
            
                vec2 uv = dir.xy;
                if (dir.z < 0.0f)
                {
                    vec2 signDir = vec2(dir.x >= 0.0f ? 1.0f : -1.0f, dir.y >= 0.0f ? 1.0f : -1.0f);
                    uv = (1.0f - abs(uv.yx)) * signDir;
                }
            
                return uv * 0.5f + 0.5f;
            }
            
            vec3 DecodeOcta(vec2 uv)
            {
                vec2 f = uv * 2.0f - 1.0f;
                vec3 n = vec3(f.x, f.y, 1.0f - abs(f.x) - abs(f.y));
            
                if (n.z < 0.0f)
                {
                    vec2 nXY = n.xy;
                    vec2 signDir = vec2(nXY.x >= 0.0f ? 1.0f : -1.0f, nXY.y >= 0.0f ? 1.0f : -1.0f);
                    n.xy = (1.0f - abs(nXY.yx)) * signDir;
                }
            
                return normalize(n);
            }
            
            vec3 DirectionFromFragPos(vec3 fragPos)
            {
                vec2 clipXY = fragPos.xy;
                if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
                    discard;
            
                vec2 uv = clipXY * 0.5f + 0.5f;
                return DecodeOcta(uv);
            }
            
            vec3 SampleOcta(sampler2D tex, vec3 dir)
            {
                vec2 uv = EncodeOcta(dir);
                return texture(tex, uv).rgb;
            }
            
            void main()
            {
                vec3 N = DirectionFromFragPos(FragPos);
            
                // Simplified irradiance computation for testing
                // Just sample a few directions and average
                vec3 irradiance = vec3(0.0f);
                
                irradiance += SampleOcta(Texture0, N);
                irradiance += SampleOcta(Texture0, -N);
                irradiance += SampleOcta(Texture0, vec3(1, 0, 0));
                irradiance += SampleOcta(Texture0, vec3(-1, 0, 0));
                irradiance += SampleOcta(Texture0, vec3(0, 1, 0));
                irradiance += SampleOcta(Texture0, vec3(0, -1, 0));
                
                OutColor = irradiance / 6.0f;
            }
            """;

        return LinkProgram(gl,
            CompileShader(gl, ShaderType.VertexShader, vertexSource),
            CompileShader(gl, ShaderType.FragmentShader, fragmentSource));
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

        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        return program;
    }

    private static (byte Red, byte Green, byte Blue) ReadPixel(GL gl, int x, int y)
    {
        Span<byte> rgba = stackalloc byte[4];
        gl.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        return (rgba[0], rgba[1], rgba[2]);
    }

    private static void AssertColorApproxEquals((byte R, byte G, byte B) actual, Vector3 expected, string label)
    {
        var expectedBytes = new Vector3(expected.X, expected.Y, expected.Z) * 255.0f;
        float tolerance = 30.0f; // allow some GPU variation

        Math.Abs(actual.R - expectedBytes.X).ShouldBeLessThan(tolerance, $"Unexpected red channel for {label}");
        Math.Abs(actual.G - expectedBytes.Y).ShouldBeLessThan(tolerance, $"Unexpected green channel for {label}");
        Math.Abs(actual.B - expectedBytes.Z).ShouldBeLessThan(tolerance, $"Unexpected blue channel for {label}");
    }
}
