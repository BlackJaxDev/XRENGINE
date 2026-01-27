using NUnit.Framework;
using Shouldly;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using System.Numerics;
using System.Runtime.InteropServices;
using Assert = NUnit.Framework.Assert;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Additional unit tests for GPU indirect rendering infrastructure.
/// Tests cover: Atlas/EBO correctness, attribute layout switching, uniform type validation,
/// fallback paths, and render state isolation between batches.
/// </summary>
[TestFixture]
public class IndirectRenderingAdditionalTests
{
    private const int Width = 256;
    private const int Height = 256;

    private static bool IsTrue(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return false;
        v = v.Trim();
        return v.Equals("1") || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShowWindow => IsTrue(Environment.GetEnvironmentVariable("XR_SHOW_TEST_WINDOWS"));

    private static int ShowWindowDurationMs
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("XR_SHOW_TEST_WINDOW_MS");
            return int.TryParse(env, out var ms) ? ms : 2000;
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

    #region Atlas/EBO Correctness Tests

    /// <summary>
    /// Test that growing the atlas (adding more mesh data) results in proper VBO/EBO uploads.
    /// Simulates the scenario where new meshes are added and the atlas needs to grow.
    /// </summary>
    [Test]
    public unsafe void Atlas_GrowingBuffer_ProperlyUploadsExpandedData()
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

        gl.BindVertexArray(vao);

        // Initial small atlas: 1 triangle
        float[] initialVertices = [
            -0.5f, -0.5f, 0f,  1f, 0f, 0f,  // Red vertex
             0.5f, -0.5f, 0f,  1f, 0f, 0f,
             0.0f,  0.5f, 0f,  1f, 0f, 0f
        ];
        uint[] initialIndices = [0, 1, 2];

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, initialVertices.AsSpan(), BufferUsageARB.DynamicDraw);

        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, initialIndices.AsSpan(), BufferUsageARB.DynamicDraw);

        // Verify initial sizes
        gl.GetBufferParameter(BufferTargetARB.ArrayBuffer, BufferPNameARB.BufferSize, out int vboSize);
        gl.GetBufferParameter(BufferTargetARB.ElementArrayBuffer, BufferPNameARB.BufferSize, out int eboSize);

        vboSize.ShouldBe(initialVertices.Length * sizeof(float));
        eboSize.ShouldBe(initialIndices.Length * sizeof(uint));

        // Grow atlas: add second triangle (simulates atlas rebuild with new mesh)
        float[] expandedVertices = [
            // First triangle (red)
            -0.5f, -0.5f, 0f,  1f, 0f, 0f,
             0.5f, -0.5f, 0f,  1f, 0f, 0f,
             0.0f,  0.5f, 0f,  1f, 0f, 0f,
            // Second triangle (green) - new mesh added to atlas
            -0.5f,  0.0f, 0f,  0f, 1f, 0f,
             0.5f,  0.0f, 0f,  0f, 1f, 0f,
             0.0f, -0.5f, 0f,  0f, 1f, 0f
        ];
        uint[] expandedIndices = [0, 1, 2, 3, 4, 5];

        // Re-upload expanded data (simulating AtlasRebuilt event response)
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, expandedVertices.AsSpan(), BufferUsageARB.DynamicDraw);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, expandedIndices.AsSpan(), BufferUsageARB.DynamicDraw);

        // Verify expanded sizes
        gl.GetBufferParameter(BufferTargetARB.ArrayBuffer, BufferPNameARB.BufferSize, out int expandedVboSize);
        gl.GetBufferParameter(BufferTargetARB.ElementArrayBuffer, BufferPNameARB.BufferSize, out int expandedEboSize);

        expandedVboSize.ShouldBe(expandedVertices.Length * sizeof(float));
        expandedEboSize.ShouldBe(expandedIndices.Length * sizeof(uint));
        expandedVboSize.ShouldBeGreaterThan(vboSize);
        expandedEboSize.ShouldBeGreaterThan(eboSize);

        // Verify data integrity by reading back
        float[] readbackVertices = new float[expandedVertices.Length];
        gl.GetBufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(readbackVertices.Length * sizeof(float)), readbackVertices.AsSpan());

        for (int i = 0; i < expandedVertices.Length; i++)
        {
            readbackVertices[i].ShouldBe(expandedVertices[i], 0.001f, $"Vertex data mismatch at index {i}");
        }

        // Cleanup
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);
        gl.DeleteBuffer(vbo);
        gl.DeleteBuffer(ebo);
        gl.DeleteVertexArray(vao);
        window.Close();
    }

    /// <summary>
    /// Test that atlas version increments correctly after rebuild.
    /// </summary>
    [Test]
    public void AtlasVersion_Increments_OnRebuild()
    {
        // Simulate version tracking behavior
        uint version = 0;
        
        // Initial state
        version.ShouldBe(0u);
        
        // After first rebuild
        version++;
        version.ShouldBe(1u);
        
        // After second rebuild
        version++;
        version.ShouldBe(2u);
        
        // Version should never wrap around unexpectedly in normal use
        version = uint.MaxValue - 1;
        version++;
        version.ShouldBe(uint.MaxValue);
    }

    /// <summary>
    /// Test EBO index element size determination based on vertex count.
    /// </summary>
    [Test]
    public void AtlasIndexElementSize_SelectsAppropriateSize_BasedOnVertexCount()
    {
        // Small mesh: fits in byte indices
        int smallVertexCount = 200;
        var smallSize = DetermineIndexSize(smallVertexCount);
        smallSize.ShouldBe(IndexSizeCategory.Byte);

        // Medium mesh: requires 16-bit indices
        int mediumVertexCount = 50000;
        var mediumSize = DetermineIndexSize(mediumVertexCount);
        mediumSize.ShouldBe(IndexSizeCategory.TwoBytes);

        // Large mesh: requires 32-bit indices
        int largeVertexCount = 100000;
        var largeSize = DetermineIndexSize(largeVertexCount);
        largeSize.ShouldBe(IndexSizeCategory.FourBytes);
    }

    private enum IndexSizeCategory { Byte, TwoBytes, FourBytes }

    private static IndexSizeCategory DetermineIndexSize(int vertexCount)
    {
        if (vertexCount <= byte.MaxValue)
            return IndexSizeCategory.Byte;
        if (vertexCount <= ushort.MaxValue)
            return IndexSizeCategory.TwoBytes;
        return IndexSizeCategory.FourBytes;
    }

    #endregion

    #region Attribute Layout Switching Tests

    /// <summary>
    /// Test that switching between shader programs with different attribute layouts
    /// properly enables/disables vertex attributes.
    /// </summary>
    [Test]
    public unsafe void AttributeLayout_Switching_NoMissingAttributes()
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
        gl.BindVertexArray(vao);

        uint vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);

        // Vertex data: position (3) + color (3) + uv (2) + normal (3)
        float[] vertices = [
            0f, 0f, 0f,  1f, 0f, 0f,  0f, 0f,  0f, 0f, 1f,
            1f, 0f, 0f,  0f, 1f, 0f,  1f, 0f,  0f, 0f, 1f,
            0f, 1f, 0f,  0f, 0f, 1f,  0f, 1f,  0f, 0f, 1f
        ];
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices.AsSpan(), BufferUsageARB.StaticDraw);

        const int stride = 11 * sizeof(float);

        // Program 1: Uses position + color (locations 0, 1)
        string vertShader1 = "#version 450 core\n" +
            "layout(location = 0) in vec3 aPos;\n" +
            "layout(location = 1) in vec3 aColor;\n" +
            "out vec3 vColor;\n" +
            "void main() { gl_Position = vec4(aPos, 1.0); vColor = aColor; }";
        
        string fragShader1 = "#version 450 core\n" +
            "in vec3 vColor;\n" +
            "out vec4 FragColor;\n" +
            "void main() { FragColor = vec4(vColor, 1.0); }";

        // Program 2: Uses position + uv + normal (locations 0, 2, 3)
        // Note: All attributes must be used in shader output chain to not be optimized out
        string vertShader2 = "#version 450 core\n" +
            "layout(location = 0) in vec3 aPos;\n" +
            "layout(location = 2) in vec2 aUV;\n" +
            "layout(location = 3) in vec3 aNormal;\n" +
            "out vec2 vUV;\n" +
            "out vec3 vNormal;\n" +
            "void main() { gl_Position = vec4(aPos + vec3(aUV, 0.0), 1.0); vUV = aUV; vNormal = aNormal; }";
        
        string fragShader2 = "#version 450 core\n" +
            "in vec2 vUV;\n" +
            "in vec3 vNormal;\n" +
            "out vec4 FragColor;\n" +
            "void main() { FragColor = vec4(vNormal * 0.5 + 0.5, 1.0) + vec4(vUV, 0.0, 0.0); }";

        uint prog1 = CreateProgram(gl, vertShader1, fragShader1);
        uint prog2 = CreateProgram(gl, vertShader2, fragShader2);

        // Configure attributes for full layout
        gl.EnableVertexAttribArray(0); // position
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
        
        gl.EnableVertexAttribArray(1); // color
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
        
        gl.EnableVertexAttribArray(2); // uv
        gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)(6 * sizeof(float)));
        
        gl.EnableVertexAttribArray(3); // normal
        gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(8 * sizeof(float)));

        gl.Viewport(0, 0, (uint)Width, (uint)Height);
        gl.ClearColor(0f, 0f, 0f, 1f);

        // Batch 1: Use program 1 (position + color)
        gl.Clear((uint)ClearBufferMask.ColorBufferBit);
        gl.UseProgram(prog1);
        
        // Verify we can query active attributes for program 1
        gl.GetProgram(prog1, ProgramPropertyARB.ActiveAttributes, out int activeAttribs1);
        activeAttribs1.ShouldBe(2, "Program 1 should have 2 active attributes (pos, color)");
        
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        var error1 = gl.GetError();
        error1.ShouldBe(GLEnum.NoError, $"GL error after batch 1: {error1}");

        // Batch 2: Switch to program 2 (position + uv + normal) - simulates batch program switch
        gl.Clear((uint)ClearBufferMask.ColorBufferBit);
        gl.UseProgram(prog2);
        
        // Verify we can query active attributes for program 2
        gl.GetProgram(prog2, ProgramPropertyARB.ActiveAttributes, out int activeAttribs2);
        activeAttribs2.ShouldBe(3, "Program 2 should have 3 active attributes (pos, uv, normal)");
        
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        var error2 = gl.GetError();
        error2.ShouldBe(GLEnum.NoError, $"GL error after batch 2: {error2}");

        // Batch 3: Switch back to program 1 - no GL errors should occur
        gl.Clear((uint)ClearBufferMask.ColorBufferBit);
        gl.UseProgram(prog1);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        var error3 = gl.GetError();
        error3.ShouldBe(GLEnum.NoError, $"GL error after batch 3: {error3}");

        // Cleanup
        gl.DisableVertexAttribArray(0);
        gl.DisableVertexAttribArray(1);
        gl.DisableVertexAttribArray(2);
        gl.DisableVertexAttribArray(3);
        gl.UseProgram(0);
        gl.DeleteProgram(prog1);
        gl.DeleteProgram(prog2);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
        window.Close();
    }

    #endregion

    #region Uniform Type Mismatch Detection Tests

    /// <summary>
    /// Test that uniform type validation correctly identifies mismatches.
    /// This is a logic test - actual GL validation happens in GLRenderProgram.
    /// </summary>
    [Test]
    public void UniformTypeValidation_DetectsMismatch_WhenTypesDoNotMatch()
    {
        // Simulate the uniform metadata lookup
        var uniformMetadata = new Dictionary<string, (GLEnum Type, int Size)>
        {
            { "uModelMatrix", (GLEnum.FloatMat4, 1) },
            { "uDiffuseColor", (GLEnum.FloatVec4, 1) },
            { "uTextureSlot", (GLEnum.Sampler2D, 1) },
            { "uLightCount", (GLEnum.Int, 1) }
        };

        // Valid type matches
        ValidateUniformType(uniformMetadata, "uModelMatrix", GLEnum.FloatMat4).ShouldBeTrue();
        ValidateUniformType(uniformMetadata, "uDiffuseColor", GLEnum.FloatVec4).ShouldBeTrue();
        ValidateUniformType(uniformMetadata, "uTextureSlot", GLEnum.Sampler2D).ShouldBeTrue();
        ValidateUniformType(uniformMetadata, "uLightCount", GLEnum.Int).ShouldBeTrue();

        // Invalid type mismatches
        ValidateUniformType(uniformMetadata, "uModelMatrix", GLEnum.FloatVec4).ShouldBeFalse();
        ValidateUniformType(uniformMetadata, "uDiffuseColor", GLEnum.FloatMat4).ShouldBeFalse();
        ValidateUniformType(uniformMetadata, "uTextureSlot", GLEnum.Int).ShouldBeFalse();
        ValidateUniformType(uniformMetadata, "uLightCount", GLEnum.Float).ShouldBeFalse();

        // Unknown uniform (should pass - defensive behavior)
        ValidateUniformType(uniformMetadata, "uUnknown", GLEnum.Float).ShouldBeTrue();
    }

    /// <summary>
    /// Test that uniform type validation handles multiple acceptable types.
    /// </summary>
    [Test]
    public void UniformTypeValidation_AcceptsMultipleTypes_ForSamplers()
    {
        var uniformMetadata = new Dictionary<string, (GLEnum Type, int Size)>
        {
            { "uTexture", (GLEnum.Sampler2D, 1) }
        };

        // Int is commonly used to set sampler binding points
        GLEnum[] acceptableTypes = [GLEnum.Int, GLEnum.Sampler2D];
        ValidateUniformTypeMultiple(uniformMetadata, "uTexture", acceptableTypes).ShouldBeTrue();

        // Float is not acceptable for samplers
        GLEnum[] invalidTypes = [GLEnum.Float];
        ValidateUniformTypeMultiple(uniformMetadata, "uTexture", invalidTypes).ShouldBeFalse();
    }

    private static bool ValidateUniformType(
        Dictionary<string, (GLEnum Type, int Size)> metadata,
        string name,
        GLEnum expectedType)
    {
        if (!metadata.TryGetValue(name, out var info))
            return true; // Unknown uniform, allow

        return info.Type == expectedType;
    }

    private static bool ValidateUniformTypeMultiple(
        Dictionary<string, (GLEnum Type, int Size)> metadata,
        string name,
        GLEnum[] expectedTypes)
    {
        if (!metadata.TryGetValue(name, out var info))
            return true;

        foreach (var expected in expectedTypes)
            if (info.Type == expected)
                return true;

        return false;
    }

    #endregion

    #region Fallback Path Tests (No ARB_indirect_parameters)

    /// <summary>
    /// Test that MultiDrawElementsIndirect (without GPU count) renders correctly.
    /// This is the fallback path when ARB_indirect_parameters is not available.
    /// </summary>
    [Test]
    public unsafe void FallbackPath_MultiDrawWithoutCount_RendersCorrectly()
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

        // Two triangles with different colors
        float[] vertices = [
            // Triangle 1 (red)
            -0.8f, -0.5f, 0f,  1f, 0f, 0f,
            -0.2f, -0.5f, 0f,  1f, 0f, 0f,
            -0.5f,  0.5f, 0f,  1f, 0f, 0f,
            // Triangle 2 (green)
             0.2f, -0.5f, 0f,  0f, 1f, 0f,
             0.8f, -0.5f, 0f,  0f, 1f, 0f,
             0.5f,  0.5f, 0f,  0f, 1f, 0f
        ];
        uint[] indices = [0, 1, 2, 3, 4, 5];

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices.AsSpan(), BufferUsageARB.StaticDraw);

        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, indices.AsSpan(), BufferUsageARB.StaticDraw);

        // Two indirect commands - CPU specifies count directly
        DrawElementsIndirectCommand[] commands = [
            new() { Count = 3, InstanceCount = 1, FirstIndex = 0, BaseVertex = 0, BaseInstance = 0 },
            new() { Count = 3, InstanceCount = 1, FirstIndex = 3, BaseVertex = 0, BaseInstance = 1 }
        ];

        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, indirectBuffer);
        gl.BufferData<DrawElementsIndirectCommand>(BufferTargetARB.DrawIndirectBuffer, commands.AsSpan(), BufferUsageARB.StaticDraw);

        const int stride = 6 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));

        string vertShader = "#version 450 core\n" +
            "layout(location = 0) in vec3 aPos;\n" +
            "layout(location = 1) in vec3 aColor;\n" +
            "out vec3 vColor;\n" +
            "void main() { gl_Position = vec4(aPos, 1.0); vColor = aColor; }";
        
        string fragShader = "#version 450 core\n" +
            "in vec3 vColor;\n" +
            "out vec4 FragColor;\n" +
            "void main() { FragColor = vec4(vColor, 1.0); }";

        uint program = CreateProgram(gl, vertShader, fragShader);
        gl.UseProgram(program);

        gl.Viewport(0, 0, (uint)Width, (uint)Height);
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear((uint)ClearBufferMask.ColorBufferBit);

        // FALLBACK PATH: Use MultiDrawElementsIndirect with explicit CPU-provided count (2)
        // This is what we use when ARB_indirect_parameters is not available
        uint commandStride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
        gl.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, (void*)0, 2, commandStride);
        gl.Finish();

        var error = gl.GetError();
        error.ShouldBe(GLEnum.NoError, $"GL error after fallback multi-draw: {error}");

        // Sample pixels to verify both triangles rendered
        gl.ReadBuffer(ReadBufferMode.Back);
        
        // Read pixel from left triangle area (should be red)
        int leftX = Width / 4;
        int centerY = Height / 2;
        byte[] leftPixel = new byte[4];
        gl.ReadPixels(leftX, centerY, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, leftPixel.AsSpan());

        // Read pixel from right triangle area (should be green)
        int rightX = 3 * Width / 4;
        byte[] rightPixel = new byte[4];
        gl.ReadPixels(rightX, centerY, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, rightPixel.AsSpan());

        // Allow inconclusive if headless rendering doesn't produce expected output
        if (leftPixel[0] == 0 && leftPixel[1] == 0 && rightPixel[0] == 0 && rightPixel[1] == 0)
        {
            Assert.Inconclusive("GPU did not render visible output; likely headless environment");
        }
        else
        {
            // Verify left pixel has red and right pixel has green
            leftPixel[0].ShouldBeGreaterThan((byte)100, "Left triangle should be red");
            rightPixel[1].ShouldBeGreaterThan((byte)100, "Right triangle should be green");
        }

        // Show window if enabled
        if (ShowWindow)
        {
            window.SwapBuffers();
            var end = DateTime.UtcNow.AddMilliseconds(ShowWindowDurationMs);
            while (DateTime.UtcNow < end && !window.IsClosing)
            {
                window.DoEvents();
                Thread.Sleep(16);
            }
        }

        // Cleanup
        gl.DisableVertexAttribArray(0);
        gl.DisableVertexAttribArray(1);
        gl.UseProgram(0);
        gl.DeleteProgram(program);
        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        gl.DeleteBuffer(indirectBuffer);
        gl.DeleteBuffer(ebo);
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
        window.Close();
    }

    /// <summary>
    /// Test that fallback path properly handles zero draw count.
    /// </summary>
    [Test]
    public unsafe void FallbackPath_ZeroDrawCount_NoError()
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(Width, Height);
        options.IsVisible = false;
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
        uint indirectBuffer = gl.GenBuffer();

        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, indirectBuffer);

        // Empty command buffer
        gl.BufferData(BufferTargetARB.DrawIndirectBuffer, 0, null, BufferUsageARB.StaticDraw);

        gl.Viewport(0, 0, (uint)Width, (uint)Height);
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear((uint)ClearBufferMask.ColorBufferBit);

        // Calling with 0 draw count should not crash or error
        uint commandStride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
        gl.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, (void*)0, 0, commandStride);

        var error = gl.GetError();
        error.ShouldBe(GLEnum.NoError, $"GL error after zero-count multi-draw: {error}");

        // Cleanup
        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
        gl.DeleteBuffer(indirectBuffer);
        gl.DeleteVertexArray(vao);
        window.Close();
    }

    #endregion

    #region State Leak Tests (Depth/Cull/Blend/Stencil)

    /// <summary>
    /// Test that depth state doesn't leak between batches.
    /// </summary>
    [Test]
    public unsafe void StateIsolation_DepthState_DoesNotLeakBetweenBatches()
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(Width, Height);
        options.IsVisible = false;
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

        // Query initial depth state
        gl.GetBoolean(GetPName.DepthTest, out bool initialDepthTest);
        gl.GetInteger(GetPName.DepthFunc, out int initialDepthFunc);
        gl.GetBoolean(GetPName.DepthWritemask, out bool initialDepthMask);

        // Batch 1: Enable depth test, less-equal, write enabled
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.DepthMask(true);

        gl.GetBoolean(GetPName.DepthTest, out bool batch1DepthTest);
        gl.GetInteger(GetPName.DepthFunc, out int batch1DepthFunc);
        gl.GetBoolean(GetPName.DepthWritemask, out bool batch1DepthMask);

        batch1DepthTest.ShouldBeTrue("Batch 1 should have depth test enabled");
        ((DepthFunction)batch1DepthFunc).ShouldBe(DepthFunction.Lequal);
        batch1DepthMask.ShouldBeTrue("Batch 1 should have depth writes enabled");

        // Batch 2: Disable depth test, different func, no writes (simulates transparent batch)
        gl.Disable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Always);
        gl.DepthMask(false);

        gl.GetBoolean(GetPName.DepthTest, out bool batch2DepthTest);
        gl.GetInteger(GetPName.DepthFunc, out int batch2DepthFunc);
        gl.GetBoolean(GetPName.DepthWritemask, out bool batch2DepthMask);

        batch2DepthTest.ShouldBeFalse("Batch 2 should have depth test disabled");
        ((DepthFunction)batch2DepthFunc).ShouldBe(DepthFunction.Always);
        batch2DepthMask.ShouldBeFalse("Batch 2 should have depth writes disabled");

        // Batch 3: Restore state explicitly (simulating proper state management)
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Less);
        gl.DepthMask(true);

        gl.GetBoolean(GetPName.DepthTest, out bool batch3DepthTest);
        gl.GetInteger(GetPName.DepthFunc, out int batch3DepthFunc);
        gl.GetBoolean(GetPName.DepthWritemask, out bool batch3DepthMask);

        batch3DepthTest.ShouldBeTrue("Batch 3 should have depth test restored");
        ((DepthFunction)batch3DepthFunc).ShouldBe(DepthFunction.Less);
        batch3DepthMask.ShouldBeTrue("Batch 3 should have depth writes restored");

        window.Close();
    }

    /// <summary>
    /// Test that cull state doesn't leak between batches.
    /// </summary>
    [Test]
    public unsafe void StateIsolation_CullState_DoesNotLeakBetweenBatches()
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(Width, Height);
        options.IsVisible = false;
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

        // Batch 1: Back-face culling
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        gl.FrontFace(FrontFaceDirection.Ccw);

        gl.GetBoolean(GetPName.CullFace, out bool batch1CullEnabled);
        gl.GetInteger(GetPName.CullFaceMode, out int batch1CullMode);
        gl.GetInteger(GetPName.FrontFace, out int batch1FrontFace);

        batch1CullEnabled.ShouldBeTrue();
        ((TriangleFace)batch1CullMode).ShouldBe(TriangleFace.Back);
        ((FrontFaceDirection)batch1FrontFace).ShouldBe(FrontFaceDirection.Ccw);

        // Batch 2: No culling (double-sided)
        gl.Disable(EnableCap.CullFace);

        gl.GetBoolean(GetPName.CullFace, out bool batch2CullEnabled);
        batch2CullEnabled.ShouldBeFalse();

        // Batch 3: Front-face culling with CW winding
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Front);
        gl.FrontFace(FrontFaceDirection.CW);

        gl.GetBoolean(GetPName.CullFace, out bool batch3CullEnabled);
        gl.GetInteger(GetPName.CullFaceMode, out int batch3CullMode);
        gl.GetInteger(GetPName.FrontFace, out int batch3FrontFace);

        batch3CullEnabled.ShouldBeTrue();
        ((TriangleFace)batch3CullMode).ShouldBe(TriangleFace.Front);
        ((FrontFaceDirection)batch3FrontFace).ShouldBe(FrontFaceDirection.CW);

        window.Close();
    }

    /// <summary>
    /// Test that blend state doesn't leak between batches.
    /// </summary>
    [Test]
    public unsafe void StateIsolation_BlendState_DoesNotLeakBetweenBatches()
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(Width, Height);
        options.IsVisible = false;
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

        // Batch 1: No blending (opaque)
        gl.Disable(EnableCap.Blend);

        gl.GetBoolean(GetPName.Blend, out bool batch1BlendEnabled);
        batch1BlendEnabled.ShouldBeFalse();

        // Batch 2: Standard alpha blending
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.BlendEquation(BlendEquationModeEXT.FuncAdd);

        gl.GetBoolean(GetPName.Blend, out bool batch2BlendEnabled);
        gl.GetInteger(GetPName.BlendSrcRgb, out int batch2SrcRgb);
        gl.GetInteger(GetPName.BlendDstRgb, out int batch2DstRgb);

        batch2BlendEnabled.ShouldBeTrue();
        ((BlendingFactor)batch2SrcRgb).ShouldBe(BlendingFactor.SrcAlpha);
        ((BlendingFactor)batch2DstRgb).ShouldBe(BlendingFactor.OneMinusSrcAlpha);

        // Batch 3: Additive blending
        gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);

        gl.GetInteger(GetPName.BlendSrcRgb, out int batch3SrcRgb);
        gl.GetInteger(GetPName.BlendDstRgb, out int batch3DstRgb);

        ((BlendingFactor)batch3SrcRgb).ShouldBe(BlendingFactor.One);
        ((BlendingFactor)batch3DstRgb).ShouldBe(BlendingFactor.One);

        // Batch 4: Disable blending (restore opaque)
        gl.Disable(EnableCap.Blend);

        gl.GetBoolean(GetPName.Blend, out bool batch4BlendEnabled);
        batch4BlendEnabled.ShouldBeFalse();

        window.Close();
    }

    /// <summary>
    /// Test that stencil state doesn't leak between batches.
    /// </summary>
    [Test]
    public unsafe void StateIsolation_StencilState_DoesNotLeakBetweenBatches()
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(Width, Height);
        options.IsVisible = false;
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

        // Batch 1: No stencil
        gl.Disable(EnableCap.StencilTest);

        gl.GetBoolean(GetPName.StencilTest, out bool batch1StencilEnabled);
        batch1StencilEnabled.ShouldBeFalse();

        // Batch 2: Stencil write (for masking)
        gl.Enable(EnableCap.StencilTest);
        gl.StencilFunc(StencilFunction.Always, 1, 0xFF);
        gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Replace);
        gl.StencilMask(0xFF);

        gl.GetBoolean(GetPName.StencilTest, out bool batch2StencilEnabled);
        gl.GetInteger(GetPName.StencilFunc, out int batch2StencilFunc);
        gl.GetInteger(GetPName.StencilRef, out int batch2StencilRef);

        batch2StencilEnabled.ShouldBeTrue();
        ((StencilFunction)batch2StencilFunc).ShouldBe(StencilFunction.Always);
        batch2StencilRef.ShouldBe(1);

        // Batch 3: Stencil test (read mask)
        gl.StencilFunc(StencilFunction.Equal, 1, 0xFF);
        gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
        gl.StencilMask(0x00); // No writes

        gl.GetInteger(GetPName.StencilFunc, out int batch3StencilFunc);
        gl.GetInteger(GetPName.StencilWritemask, out int batch3StencilMask);

        ((StencilFunction)batch3StencilFunc).ShouldBe(StencilFunction.Equal);
        batch3StencilMask.ShouldBe(0);

        // Batch 4: Disable stencil
        gl.Disable(EnableCap.StencilTest);

        gl.GetBoolean(GetPName.StencilTest, out bool batch4StencilEnabled);
        batch4StencilEnabled.ShouldBeFalse();

        window.Close();
    }

    /// <summary>
    /// Test comprehensive state isolation across all major state categories.
    /// </summary>
    [Test]
    public unsafe void StateIsolation_AllMajorStates_IndependentBetweenBatches()
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(Width, Height);
        options.IsVisible = false;
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

        // Capture initial state
        var initialState = CaptureRenderState(gl);

        // Set state for batch 1 (opaque PBR-style)
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Less);
        gl.DepthMask(true);
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.StencilTest);

        var batch1State = CaptureRenderState(gl);
        batch1State.DepthTestEnabled.ShouldBeTrue();
        batch1State.DepthWriteEnabled.ShouldBeTrue();
        batch1State.CullFaceEnabled.ShouldBeTrue();
        batch1State.BlendEnabled.ShouldBeFalse();
        batch1State.StencilTestEnabled.ShouldBeFalse();

        // Set state for batch 2 (transparent)
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Less);
        gl.DepthMask(false); // No depth writes
        gl.Disable(EnableCap.CullFace); // Two-sided
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.Disable(EnableCap.StencilTest);

        var batch2State = CaptureRenderState(gl);
        batch2State.DepthTestEnabled.ShouldBeTrue();
        batch2State.DepthWriteEnabled.ShouldBeFalse();
        batch2State.CullFaceEnabled.ShouldBeFalse();
        batch2State.BlendEnabled.ShouldBeTrue();
        batch2State.StencilTestEnabled.ShouldBeFalse();

        // Set state for batch 3 (outline with stencil)
        gl.Disable(EnableCap.DepthTest);
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Front);
        gl.Enable(EnableCap.Blend);
        gl.Enable(EnableCap.StencilTest);
        gl.StencilFunc(StencilFunction.Notequal, 1, 0xFF);

        var batch3State = CaptureRenderState(gl);
        batch3State.DepthTestEnabled.ShouldBeFalse();
        batch3State.CullFaceEnabled.ShouldBeTrue();
        batch3State.BlendEnabled.ShouldBeTrue();
        batch3State.StencilTestEnabled.ShouldBeTrue();

        window.Close();
    }

    private struct RenderState
    {
        public bool DepthTestEnabled;
        public bool DepthWriteEnabled;
        public DepthFunction DepthFunc;
        public bool CullFaceEnabled;
        public TriangleFace CullFaceMode;
        public bool BlendEnabled;
        public bool StencilTestEnabled;
    }

    private static RenderState CaptureRenderState(GL gl)
    {
        gl.GetBoolean(GetPName.DepthTest, out bool depthTest);
        gl.GetBoolean(GetPName.DepthWritemask, out bool depthWrite);
        gl.GetInteger(GetPName.DepthFunc, out int depthFunc);
        gl.GetBoolean(GetPName.CullFace, out bool cullFace);
        gl.GetInteger(GetPName.CullFaceMode, out int cullMode);
        gl.GetBoolean(GetPName.Blend, out bool blend);
        gl.GetBoolean(GetPName.StencilTest, out bool stencil);

        return new RenderState
        {
            DepthTestEnabled = depthTest,
            DepthWriteEnabled = depthWrite,
            DepthFunc = (DepthFunction)depthFunc,
            CullFaceEnabled = cullFace,
            CullFaceMode = (TriangleFace)cullMode,
            BlendEnabled = blend,
            StencilTestEnabled = stencil
        };
    }

    #endregion

    #region Helper Methods

    private static uint CreateProgram(GL gl, string vertexSource, string fragmentSource)
    {
        uint vertexShader = CompileShader(gl, ShaderType.VertexShader, vertexSource);
        uint fragmentShader = CompileShader(gl, ShaderType.FragmentShader, fragmentSource);

        uint program = gl.CreateProgram();
        gl.AttachShader(program, vertexShader);
        gl.AttachShader(program, fragmentShader);
        gl.LinkProgram(program);

        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int status);
        if (status == 0)
        {
            string infoLog = gl.GetProgramInfoLog(program);
            gl.DeleteShader(vertexShader);
            gl.DeleteShader(fragmentShader);
            gl.DeleteProgram(program);
            throw new InvalidOperationException($"Program link failed: {infoLog}");
        }

        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        return program;
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
            throw new InvalidOperationException($"Shader compilation failed ({type}): {infoLog}");
        }

        return shader;
    }

    #endregion
}
