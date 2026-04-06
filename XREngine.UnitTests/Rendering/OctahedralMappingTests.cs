using System;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class OctahedralMappingTests
{
    private const float SourceEpsilon = 1e-5f;
    private const float RoundTripDotThreshold = 0.9999f;

    private static string ShaderBasePath
    {
        get
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                string candidate = Path.Combine(dir, "Build", "CommonAssets", "Shaders");
                if (Directory.Exists(candidate))
                    return candidate;

                dir = Path.GetDirectoryName(dir) ?? dir;
            }

            return @"D:\Documents\XRENGINE\Build\CommonAssets\Shaders";
        }
    }

    private static string RepoRoot
        => Directory.GetParent(ShaderBasePath)!.Parent!.Parent!.FullName;

    [Test]
    public void SharedOctahedralMapping_UsesDirectL1Projection()
    {
        string functionBlock = ExtractFunctionBlock(
            LoadTextFile(Path.Combine(ShaderBasePath, "Snippets", "OctahedralMapping.glsl")),
            "vec2 XRENGINE_EncodeOcta(vec3 dir)");

        functionBlock.ShouldContain("vec3 octDir = vec3(dir.x, dir.z, dir.y);");
        functionBlock.ShouldContain("octDir /= max(abs(octDir.x) + abs(octDir.y) + abs(octDir.z), 1e-5);");
        functionBlock.ShouldNotContain("normalize(dir)");
    }

    [Test]
    public void ForwardLightingOctaEncode_MatchesSharedHelper()
    {
        string sharedFunction = ExtractFunctionBlock(
            LoadTextFile(Path.Combine(ShaderBasePath, "Snippets", "OctahedralMapping.glsl")),
            "vec2 XRENGINE_EncodeOcta(vec3 dir)");
        string forwardLightingFunction = ExtractFunctionBlock(
            LoadTextFile(Path.Combine(ShaderBasePath, "Snippets", "ForwardLighting.glsl")),
            "vec2 XRENGINE_EncodeOcta(vec3 dir)");

        forwardLightingFunction.ShouldBe(sharedFunction);
    }

    [Test]
    public void SkyboxShaderAndFallback_StayAligned()
    {
        string shaderFunction = ExtractFunctionBlock(
            LoadTextFile(Path.Combine(ShaderBasePath, "Scene3D", "SkyboxOctahedral.fs")),
            "vec2 EncodeOcta(vec3 dir)");
        string fallbackFunction = ExtractFunctionBlock(
            LoadTextFile(Path.Combine(RepoRoot, "XRENGINE", "Scene", "Components", "Misc", "SkyboxComponent.cs")),
            "vec2 EncodeOcta(vec3 dir)");

        shaderFunction.ShouldBe(fallbackFunction);
        shaderFunction.ShouldNotContain("normalize(dir)");
    }

    [TestCase(0.0f, 1.0f, 0.0f, 0.5f, 0.5f)]
    [TestCase(1.0f, 0.0f, 0.0f, 1.0f, 0.5f)]
    [TestCase(-1.0f, 0.0f, 0.0f, 0.0f, 0.5f)]
    [TestCase(0.0f, 0.0f, 1.0f, 0.5f, 1.0f)]
    [TestCase(0.0f, 0.0f, -1.0f, 0.5f, 0.0f)]
    [TestCase(0.0f, -1.0f, 0.0f, 1.0f, 1.0f)]
    public void EncodeOcta_MapsCanonicalWorldAxesToExpectedTexels(float x, float y, float z, float expectedU, float expectedV)
    {
        Vector2 encoded = EncodeOcta(new Vector3(x, y, z));

        encoded.X.ShouldBe(expectedU, 1e-6f);
        encoded.Y.ShouldBe(expectedV, 1e-6f);
    }

    [Test]
    public void DecodeOcta_RoundTripsCanonicalAndSeamDirections()
    {
        Vector3[] directions =
        [
            new(1.0f, 0.0f, 0.0f),
            new(-1.0f, 0.0f, 0.0f),
            new(0.0f, 1.0f, 0.0f),
            new(0.0f, -1.0f, 0.0f),
            new(0.0f, 0.0f, 1.0f),
            new(0.0f, 0.0f, -1.0f),
            new(0.57735026f, 0.57735026f, 0.57735026f),
            new(-0.4082483f, 0.8164966f, -0.4082483f),
            new(0.123f, -0.456f, 0.881f),
            new(-0.701f, 0.102f, -0.706f),
        ];

        foreach (Vector3 direction in directions)
            AssertRoundTrip(direction);
    }

    [Test]
    public void DecodeOcta_RoundTripsDeterministicDirectionCoverage()
    {
        Random random = new(4242);
        for (int i = 0; i < 1024; i++)
        {
            Vector3 direction = new(
                NextSignedFloat(random),
                NextSignedFloat(random),
                NextSignedFloat(random));

            if (direction.LengthSquared() < 1e-8f)
            {
                i--;
                continue;
            }

            AssertRoundTrip(direction);
        }
    }

    [Test]
    public void ZeroVector_EncodesToCenterAndDecodesToPositiveY()
    {
        Vector2 encoded = EncodeOcta(Vector3.Zero);
        Vector3 decoded = DecodeOcta(encoded);

        encoded.X.ShouldBe(0.5f, 1e-6f);
        encoded.Y.ShouldBe(0.5f, 1e-6f);
        decoded.X.ShouldBe(0.0f, 1e-6f);
        decoded.Y.ShouldBe(1.0f, 1e-6f);
        decoded.Z.ShouldBe(0.0f, 1e-6f);
    }

    [TestCase("Scene3D/CubemapToOctahedron.fs")]
    [TestCase("Scene3D/SkyboxOctahedral.fs")]
    public void RepresentativeOctahedralShaders_CompileToSpirv(string shaderRelativePath)
    {
        string fullPath = ResolveShaderPath(shaderRelativePath);
        var shaderSource = new TextFile
        {
            FilePath = fullPath,
            Text = File.ReadAllText(fullPath)
        };

        XRShader shader = new(EShaderType.Fragment, shaderSource);
        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out _);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
    }

    private static void AssertRoundTrip(Vector3 direction)
    {
        Vector3 normalized = Normalize(direction);
        Vector3 decoded = DecodeOcta(EncodeOcta(direction));
        float dot = Vector3.Dot(normalized, decoded);

        dot.ShouldBeGreaterThan(RoundTripDotThreshold);
        decoded.Length().ShouldBe(1.0f, 1e-5f);
    }

    private static Vector2 EncodeOcta(Vector3 dir)
    {
        Vector3 octDir = new(dir.X, dir.Z, dir.Y);
        float invL1Norm = 1.0f / MathF.Max(MathF.Abs(octDir.X) + MathF.Abs(octDir.Y) + MathF.Abs(octDir.Z), SourceEpsilon);
        octDir *= invL1Norm;

        Vector2 uv = new(octDir.X, octDir.Y);
        if (octDir.Z < 0.0f)
        {
            Vector2 signDir = new(SignNotZero(octDir.X), SignNotZero(octDir.Y));
            uv = new Vector2(1.0f - MathF.Abs(uv.Y), 1.0f - MathF.Abs(uv.X)) * signDir;
        }

        return uv * 0.5f + new Vector2(0.5f, 0.5f);
    }

    private static Vector3 DecodeOcta(Vector2 uv)
    {
        Vector2 f = uv * 2.0f - Vector2.One;
        Vector3 n = new(f.X, f.Y, 1.0f - MathF.Abs(f.X) - MathF.Abs(f.Y));

        if (n.Z < 0.0f)
        {
            Vector2 signDir = new(SignNotZero(n.X), SignNotZero(n.Y));
            Vector2 folded = new(1.0f - MathF.Abs(n.Y), 1.0f - MathF.Abs(n.X));
            n.X = folded.X * signDir.X;
            n.Y = folded.Y * signDir.Y;
        }

        return Normalize(new Vector3(n.X, n.Z, n.Y));
    }

    private static string LoadTextFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}", filePath);

        return File.ReadAllText(filePath);
    }

    private static string ResolveShaderPath(string relativePath)
        => Path.Combine(ShaderBasePath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string ExtractFunctionBlock(string source, string signature)
    {
        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        if (signatureIndex < 0)
            throw new InvalidOperationException($"Function signature '{signature}' not found.");

        int openBraceIndex = source.IndexOf('{', signatureIndex);
        if (openBraceIndex < 0)
            throw new InvalidOperationException($"Opening brace not found for '{signature}'.");

        int depth = 0;
        for (int i = openBraceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[signatureIndex..(i + 1)];
            }
        }

        throw new InvalidOperationException($"Closing brace not found for '{signature}'.");
    }

    private static Vector3 Normalize(Vector3 value)
    {
        float lengthSquared = value.LengthSquared();
        if (lengthSquared <= 1e-12f)
            return Vector3.Zero;

        return value / MathF.Sqrt(lengthSquared);
    }

    private static float NextSignedFloat(Random random)
        => (float)(random.NextDouble() * 2.0 - 1.0);

    private static float SignNotZero(float value)
        => value >= 0.0f ? 1.0f : -1.0f;
}