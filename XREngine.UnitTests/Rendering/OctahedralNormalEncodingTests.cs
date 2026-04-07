using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class OctahedralNormalEncodingTests
{
    private const float SourceEpsilon = 1e-6f;
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

    [Test]
    public void NormalEncodingSnippet_UsesDirectL1Projection()
    {
        string source = LoadShaderSource("Snippets/NormalEncoding.glsl");

        source.ShouldContain("float invL1Norm = 1.0f / max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1e-6f);");
        source.ShouldContain("vec3 n = normal * invL1Norm;");
        source.ShouldContain("float t = clamp(-n.z, 0.0f, 1.0f);");
        source.ShouldNotContain("vec3 n = normalize(normal);");
    }

    [TestCase(0.0f, 0.0f, 1.0f, 0.5f, 0.5f)]
    [TestCase(1.0f, 0.0f, 0.0f, 1.0f, 0.5f)]
    [TestCase(-1.0f, 0.0f, 0.0f, 0.0f, 0.5f)]
    [TestCase(0.0f, 1.0f, 0.0f, 0.5f, 1.0f)]
    [TestCase(0.0f, -1.0f, 0.0f, 0.5f, 0.0f)]
    [TestCase(0.0f, 0.0f, -1.0f, 1.0f, 1.0f)]
    public void EncodeNormal_MapsCanonicalAxesToExpectedTexels(float x, float y, float z, float expectedU, float expectedV)
    {
        Vector2 encoded = EncodeNormal(new Vector3(x, y, z));

        encoded.X.ShouldBe(expectedU, 1e-6f);
        encoded.Y.ShouldBe(expectedV, 1e-6f);
    }

    [Test]
    public void DecodeNormal_RoundTripsCanonicalAndSeamDirections()
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
            new(0.999f, 0.001f, -0.001f),
            new(-0.001f, -0.999f, 0.001f),
        ];

        foreach (Vector3 direction in directions)
        {
            AssertRoundTrip(direction);
        }
    }

    [Test]
    public void DecodeNormal_RoundTripsDeterministicHemisphereCoverage()
    {
        Random random = new(1337);
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
    public void ZeroVector_EncodesToCenterAndDecodesToPositiveZ()
    {
        Vector2 encoded = EncodeNormal(Vector3.Zero);
        Vector3 decoded = DecodeNormal(encoded);

        encoded.X.ShouldBe(0.5f, 1e-6f);
        encoded.Y.ShouldBe(0.5f, 1e-6f);
        decoded.X.ShouldBe(0.0f, 1e-6f);
        decoded.Y.ShouldBe(0.0f, 1e-6f);
        decoded.Z.ShouldBe(1.0f, 1e-6f);
    }

    [TestCase("Common/TexturedDeferred.fs")]
    [TestCase("Common/TexturedAlphaDeferred.fs")]
    public void RepresentativeShaders_CompileWithUpdatedNormalEncoding(string shaderRelativePath)
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
        Vector3 decoded = DecodeNormal(EncodeNormal(direction));
        float dot = Vector3.Dot(normalized, decoded);

        dot.ShouldBeGreaterThan(RoundTripDotThreshold);
        decoded.Length().ShouldBe(1.0f, 1e-5f);
    }

    private static string LoadShaderSource(string relativePath)
    {
        string fullPath = ResolveShaderPath(relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Shader file not found: {fullPath}");

        return File.ReadAllText(fullPath);
    }

    private static string ResolveShaderPath(string relativePath)
        => Path.Combine(ShaderBasePath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static Vector2 EncodeNormal(Vector3 normal)
    {
        float invL1Norm = 1.0f / MathF.Max(MathF.Abs(normal.X) + MathF.Abs(normal.Y) + MathF.Abs(normal.Z), SourceEpsilon);
        Vector3 n = normal * invL1Norm;

        Vector2 oct = new(n.X, n.Y);
        if (n.Z < 0.0f)
        {
            Vector2 signDir = new(SignNotZero(n.X), SignNotZero(n.Y));
            oct = new Vector2(1.0f - MathF.Abs(oct.Y), 1.0f - MathF.Abs(oct.X)) * signDir;
        }

        return oct * 0.5f + new Vector2(0.5f, 0.5f);
    }

    private static Vector3 DecodeNormal(Vector2 encoded)
    {
        Vector2 f = encoded * 2.0f - Vector2.One;
        Vector3 n = new(f, 1.0f - MathF.Abs(f.X) - MathF.Abs(f.Y));
        float t = Math.Clamp(-n.Z, 0.0f, 1.0f);
        n.X -= n.X >= 0.0f ? t : -t;
        n.Y -= n.Y >= 0.0f ? t : -t;
        return Normalize(n);
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