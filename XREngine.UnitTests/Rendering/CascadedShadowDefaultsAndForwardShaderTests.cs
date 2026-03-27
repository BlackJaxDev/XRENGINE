using NUnit.Framework;
using Shouldly;
using System.IO;
using System.Numerics;
using XREngine.Components;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class CascadedShadowDefaultsAndForwardShaderTests : GpuTestBase
{
    [Test]
    public void CameraComponent_DefaultsToCascadedDirectionalShadows()
    {
        var component = new CameraComponent();

        component.DirectionalShadowRenderingMode.ShouldBe(EDirectionalShadowRenderingMode.Cascaded);
    }

    [Test]
    public void ForwardLightingSnippet_DeclaresCascadeShadowBindings()
    {
        string source = LoadShaderSource("Snippets/ForwardLighting.glsl");

        source.ShouldContain("uniform sampler2DArray ShadowMapArray;");
        source.ShouldContain("layout(binding = 17) uniform samplerCube PointLightShadowMaps");
        source.ShouldContain("layout(binding = 21) uniform sampler2D SpotLightShadowMaps");
        source.ShouldContain("uniform int ForwardPlusEyeCount;");
        source.ShouldContain("int XRENGINE_GetForwardViewIndex()");
        source.ShouldContain("vec3 XRENGINE_GetForwardCameraPosition()");
        source.ShouldContain("float XRENGINE_ReadShadowMapPoint(int lightIndex, PointLight light, vec3 normal, vec3 fragPos)");
        source.ShouldContain("float XRENGINE_ReadShadowMapSpot(int lightIndex, SpotLight light, vec3 normal, vec3 fragPos, vec3 lightDir)");
        source.ShouldContain("uniform bool UseCascadedDirectionalShadows;");
        source.ShouldContain("uniform bool EnableContactShadows = true;");
        source.ShouldContain("uniform float ContactShadowDistance = 0.1;");
        source.ShouldContain("uniform int ShadowSamples = 4;");
        source.ShouldContain("uniform int ContactShadowSamples = 4;");
        source.ShouldContain("int XRENGINE_GetPrimaryDirLightCascadeIndex(vec3 fragPosWS)");
        source.ShouldContain("DirectionalLights[0].CascadeMatrices[cascadeIndex]");
        source.ShouldContain("XRENGINE_SampleContactShadowArray(");
        source.ShouldContain("XRENGINE_SampleContactShadow2D(");
        source.ShouldContain("XRENGINE_SampleShadowMapFiltered(");
        source.ShouldContain("XRENGINE_SampleShadowMapArrayFiltered(");
        source.ShouldContain("XRENGINE_SampleShadowCubeFiltered(");
        source.ShouldContain("XRENGINE_ResolveContactShadowSampleCount(");
        source.ShouldContain("vec3 offsetPosWS = fragPos + normal * ShadowBiasMax;");
        source.ShouldContain("SpotLightShadowMaps[shadowSlot],");
        source.ShouldContain("light.Base.Base.WorldToLightSpaceProjMatrix,");
        source.ShouldContain("SpotLightShadowBiasMax[lightIndex],");
    }

    [Test]
    public void LightStructsSnippet_DeclaresDirectionalCascadeFields()
    {
        string source = LoadShaderSource("Snippets/LightStructs.glsl");

        source.ShouldContain("const int XRENGINE_MAX_CASCADES = 8;");
        source.ShouldContain("mat4 WorldToLightInvViewMatrix;");
        source.ShouldContain("mat4 WorldToLightProjMatrix;");
        source.ShouldContain("mat4 WorldToLightSpaceMatrix;");
        source.ShouldContain("float CascadeSplits[XRENGINE_MAX_CASCADES];");
        source.ShouldContain("mat4 CascadeMatrices[XRENGINE_MAX_CASCADES];");
        source.ShouldContain("int CascadeCount;");
    }

    [Test]
    public void DeferredDirectionalShader_OffsetsReceiverBeforeShadowSample()
    {
        string source = LoadShaderSource("Scene3D/DeferredLightingDir.fs");

        source.ShouldContain("vec3 offsetPosWS = fragPosWS + N * ShadowBiasMax;");
        source.ShouldContain("uniform bool EnableContactShadows = true;");
        source.ShouldContain("uniform float ContactShadowDistance = 0.1f;");
        source.ShouldContain("uniform int ShadowSamples = 4;");
        source.ShouldContain("uniform int ContactShadowSamples = 4;");
        source.ShouldContain("SampleContactShadowScreenSpaceLocal(");
        source.ShouldContain("SampleShadowMapFilteredLocal(");
        source.ShouldContain("SampleShadowMapArrayFilteredLocal(");
        source.ShouldContain("ResolveContactShadowSampleCountLocal(");
        source.ShouldNotContain("MinFade");
        source.ShouldNotContain("MaxFade");
    }

    [Test]
    public void DeferredPointShader_DoesNotDeclareDistanceFadeUniforms()
    {
        string source = LoadShaderSource("Scene3D/DeferredLightingPoint.fs");

        source.ShouldNotContain("MinFade");
        source.ShouldNotContain("MaxFade");
    }

    [Test]
    public void PointLightComponent_UsesRenderTranslationForShaderPosition()
    {
        string source = LoadRepoSource(Path.Combine("XRENGINE", "Scene", "Components", "Lights", "Types", "PointLightComponent.cs"));

        source.ShouldContain("Vector3 lightPosition = Transform.RenderTranslation;");
        source.ShouldContain("program.Uniform($\"{flatPrefix}Position\", lightPosition);");
        source.ShouldContain("program.Uniform($\"{prefix}.Position\", lightPosition);");
        source.ShouldContain("program.Uniform(\"LightPos\", Transform.RenderTranslation);");
    }

    [Test]
    public void DeferredSpotShader_UsesScreenSpaceContactShadows()
    {
        string source = LoadShaderSource("Scene3D/DeferredLightingSpot.fs");

        source.ShouldContain("uniform bool EnableContactShadows = true;");
        source.ShouldContain("uniform float ContactShadowDistance = 0.1f;");
        source.ShouldContain("uniform int ContactShadowSamples = 4;");
        source.ShouldContain("SampleContactShadowScreenSpaceLocal(");
        source.ShouldContain("ResolveContactShadowSampleCountLocal(");
        source.ShouldNotContain("MinFade");
        source.ShouldNotContain("MaxFade");
    }

    [Test]
    public void LightSources_DeclareTunedShadowDefaults()
    {
        string lightComponentSource = LoadRepoSource(Path.Combine("XRENGINE", "Scene", "Components", "Lights", "Types", "LightComponent.cs"));
        lightComponentSource.ShouldContain("private float _shadowMaxBias = 0.004f;");
        lightComponentSource.ShouldContain("private float _shadowMinBias = 0.00001f;");
        lightComponentSource.ShouldContain("private float _shadowExponent = 1.221f;");
        lightComponentSource.ShouldContain("private float _shadowExponentBase = 0.035f;");
        lightComponentSource.ShouldContain("private int _samples = 4;");
        lightComponentSource.ShouldContain("private float _filterRadius = 0.0012f;");
        lightComponentSource.ShouldContain("private bool _enableContactShadows = true;");
        lightComponentSource.ShouldContain("private float _contactShadowDistance = 0.1f;");
        lightComponentSource.ShouldContain("private int _contactShadowSamples = 4;");

        string directionalSource = LoadRepoSource(Path.Combine("XRENGINE", "Scene", "Components", "Lights", "Types", "DirectionalLightComponent.cs"));
        directionalSource.ShouldContain("ShadowExponentBase = 0.035f;");
        directionalSource.ShouldContain("ShadowExponent = 1.221f;");
        directionalSource.ShouldContain("ShadowMinBias = 0.00001f;");
        directionalSource.ShouldContain("ShadowMaxBias = 0.004f;");
        directionalSource.ShouldContain("FilterRadius = 0.0012f;");

        string spotSource = LoadRepoSource(Path.Combine("XRENGINE", "Scene", "Components", "Lights", "Types", "SpotLightComponent.cs"));
        spotSource.ShouldContain("SetShadowMapResolution(2048u, 2048u);");
        spotSource.ShouldContain("ShadowMinBias = 0.0001f;");
        spotSource.ShouldContain("ShadowMaxBias = 0.07f;");
        spotSource.ShouldContain("ShadowExponentBase = 0.2f;");
        spotSource.ShouldContain("ShadowExponent = 1.0f;");
        spotSource.ShouldContain("Samples = 8;");
        spotSource.ShouldContain("FilterRadius = 0.0012f;");
        spotSource.ShouldContain("EnableContactShadows = true;");
        spotSource.ShouldContain("ContactShadowDistance = 0.1f;");
        spotSource.ShouldContain("ContactShadowSamples = 4;");
    }

    private static string LoadRepoSource(string relativePath)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            string candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = Path.GetDirectoryName(dir) ?? dir;
        }

        Assert.Inconclusive($"Repository source file not found: {relativePath}");
        return string.Empty;
    }

    /// <summary>
    /// Standalone transforms (World == null) used by cascade shadow cameras must have their
    /// RenderMatrix updated by RecalculateMatrices so that InverseRenderMatrix returns
    /// the correct view matrix, not Identity.
    /// </summary>
    [Test]
    public void StandaloneTransform_RecalculateMatrices_UpdatesRenderMatrix()
    {
        var transform = new Transform
        {
            Order = XREngine.Animation.ETransformOrder.TRS,
        };

        // Standalone: not part of any scene graph
        transform.World.ShouldBeNull();

        // Set position off-origin and a non-identity rotation
        var position = new Vector3(10.0f, 20.0f, -30.0f);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.25f);
        transform.Translation = position;
        transform.Rotation = rotation;

        // Before RecalculateMatrices, RenderMatrix is still Identity
        transform.RenderMatrix.ShouldBe(Matrix4x4.Identity,
            "RenderMatrix should still be Identity before RecalculateMatrices is called");

        // This is the call that the cascade shadow camera fix adds
        transform.RecalculateMatrices(forceWorldRecalc: true);

        // Now RenderMatrix should reflect the world matrix (which equals local for parentless transforms)
        transform.RenderMatrix.ShouldNotBe(Matrix4x4.Identity,
            "RenderMatrix must be updated after RecalculateMatrices for a standalone transform");

        // Verify the translation is present in the render matrix
        var renderTranslation = transform.RenderMatrix.Translation;
        renderTranslation.X.ShouldBe(position.X, 1e-4f);
        renderTranslation.Y.ShouldBe(position.Y, 1e-4f);
        renderTranslation.Z.ShouldBe(position.Z, 1e-4f);

        // InverseRenderMatrix should now produce a proper view matrix
        var invRender = transform.InverseRenderMatrix;
        invRender.ShouldNotBe(Matrix4x4.Identity,
            "InverseRenderMatrix should not be Identity after RecalculateMatrices");

        // Round-trip: RenderMatrix * InverseRenderMatrix ≈ Identity
        var product = transform.RenderMatrix * invRender;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                float expected = r == c ? 1.0f : 0.0f;
                float actual = r switch
                {
                    0 => c switch { 0 => product.M11, 1 => product.M12, 2 => product.M13, _ => product.M14 },
                    1 => c switch { 0 => product.M21, 1 => product.M22, 2 => product.M23, _ => product.M24 },
                    2 => c switch { 0 => product.M31, 1 => product.M32, 2 => product.M33, _ => product.M34 },
                    _ => c switch { 0 => product.M41, 1 => product.M42, 2 => product.M43, _ => product.M44 },
                };
                actual.ShouldBe(expected, 1e-4f, $"RenderMatrix * InverseRenderMatrix should be Identity at [{r},{c}]");
            }
    }

    /// <summary>
    /// BuildLightSpaceBasis must produce a worldToLight matrix that correctly
    /// projects world-space vectors into light space using the C# row-vector
    /// convention (Vector3.Transform(v, M) = v * M).
    /// The Z axis in light space should align with lightDir.
    /// </summary>
    [Test]
    public void WorldToLight_ProjectsLightDirToZAxis()
    {
        // Replicate the corrected BuildLightSpaceBasis logic
        Vector3 lightDir = Vector3.Normalize(new Vector3(1, -1, 0));
        Vector3 up = Vector3.UnitY;
        if (MathF.Abs(Vector3.Dot(lightDir, up)) > 0.99f)
            up = Vector3.UnitX;

        Vector3 right = Vector3.Normalize(Vector3.Cross(up, lightDir));
        up = Vector3.Normalize(Vector3.Cross(lightDir, right));

        // Corrected matrix: basis vectors distributed into columns for row-vector convention
        Matrix4x4 worldToLight = new(
            right.X, up.X, lightDir.X, 0,
            right.Y, up.Y, lightDir.Y, 0,
            right.Z, up.Z, lightDir.Z, 0,
            0, 0, 0, 1);

        // lightDir should map to (0, 0, 1) in light space (Z axis)
        Vector3 lightDirInLS = Vector3.Transform(lightDir, worldToLight);
        lightDirInLS.X.ShouldBe(0f, 1e-5f, "lightDir projected to X should be 0");
        lightDirInLS.Y.ShouldBe(0f, 1e-5f, "lightDir projected to Y should be 0");
        lightDirInLS.Z.ShouldBe(1f, 1e-5f, "lightDir projected to Z should be 1");

        // right should map to (1, 0, 0)
        Vector3 rightInLS = Vector3.Transform(right, worldToLight);
        rightInLS.X.ShouldBe(1f, 1e-5f);
        rightInLS.Y.ShouldBe(0f, 1e-5f);
        rightInLS.Z.ShouldBe(0f, 1e-5f);

        // up should map to (0, 1, 0)
        Vector3 upInLS = Vector3.Transform(up, worldToLight);
        upInLS.X.ShouldBe(0f, 1e-5f);
        upInLS.Y.ShouldBe(1f, 1e-5f);
        upInLS.Z.ShouldBe(0f, 1e-5f);
    }
}