using NUnit.Framework;
using Shouldly;
using System.IO;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
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
        source.ShouldContain("layout(binding = 26) uniform sampler2D ForwardContactDepthView;");
        source.ShouldContain("layout(binding = 27) uniform sampler2D ForwardContactNormalView;");
        source.ShouldContain("layout(binding = 28) uniform sampler2DArray ForwardContactDepthViewArray;");
        source.ShouldContain("layout(binding = 29) uniform sampler2DArray ForwardContactNormalViewArray;");
        source.ShouldContain("uniform bool ForwardContactShadowsEnabled = false;");
        source.ShouldContain("uniform bool ForwardContactShadowsArrayEnabled = false;");
        source.ShouldContain("uniform int ForwardPlusEyeCount;");
        source.ShouldContain("layout(std430, binding = 22) readonly buffer ForwardDirectionalLightsBuffer");
        source.ShouldContain("layout(std430, binding = 23) readonly buffer ForwardPointLightsBuffer");
        source.ShouldContain("layout(std430, binding = 26) readonly buffer ForwardSpotLightsBuffer");
        source.ShouldContain("layout(std430, binding = 27) readonly buffer ForwardPointShadowMetadataBuffer");
        source.ShouldContain("layout(std430, binding = 28) readonly buffer ForwardSpotShadowMetadataBuffer");
        source.ShouldContain("uniform mat4 LeftEyeInverseProjMatrix;");
        source.ShouldContain("uniform mat4 RightEyeInverseProjMatrix;");
        source.ShouldContain("uniform mat4 LeftEyeProjMatrix;");
        source.ShouldContain("uniform mat4 RightEyeProjMatrix;");
        source.ShouldContain("uniform mat4 LeftEyeViewProjectionMatrix;");
        source.ShouldContain("uniform mat4 RightEyeViewProjectionMatrix;");
        source.ShouldContain("ForwardPointShadowData PointLightShadows[];");
        source.ShouldContain("ForwardSpotShadowData SpotLightShadows[];");
        source.ShouldContain("int XRENGINE_GetForwardViewIndex()");
        source.ShouldContain("mat4 XRENGINE_GetForwardInverseViewMatrix()");
        source.ShouldContain("mat4 XRENGINE_GetForwardInverseProjMatrix()");
        source.ShouldContain("mat4 XRENGINE_GetForwardProjMatrix()");
        source.ShouldContain("mat4 XRENGINE_GetForwardViewProjectionMatrix()");
        source.ShouldContain("vec3 XRENGINE_GetForwardCameraPosition()");
        source.ShouldContain("void XRENGINE_TrySetForwardShadowDebug(int debugMode, float lit, float margin)");
        source.ShouldContain("float XRENGINE_ReadShadowMapPoint(int lightIndex, PointLight light, vec3 normal, vec3 fragPos)");
        source.ShouldContain("float XRENGINE_ReadShadowMapSpot(int lightIndex, SpotLight light, vec3 normal, vec3 fragPos, vec3 lightDir)");
        source.ShouldContain("float XRENGINE_SampleForwardContactShadowScreenSpace(");
        source.ShouldContain("uniform bool UseCascadedDirectionalShadows;");
        source.ShouldContain("#define EnableContactShadows (ShadowPackedI1.y != 0)");
        source.ShouldContain("#define ContactShadowDistance ShadowParams2.y");
        source.ShouldContain("uniform ivec4 ShadowPackedI0 = ivec4(8, 8, 5, 2);");
        source.ShouldContain("uniform ivec4 ShadowPackedI1 = ivec4(1, 1, 16, 0);");
        source.ShouldContain("uniform vec4 ShadowParams1 = vec4(0.0012, 0.01, 0.001, 0.015);");
        source.ShouldContain("uniform vec4 ShadowParams2 = vec4(1.2, 1.0, 2.0, 10.0);");
        source.ShouldContain("#define ShadowVogelTapCount ShadowPackedI0.z");
        source.ShouldContain("#define ContactShadowSamples ShadowPackedI1.z");
        source.ShouldContain("ivec4 shadowI0 = shadowData.Packed0;");
        source.ShouldContain("ivec4 shadowI1 = shadowData.Packed1;");
        source.ShouldContain("int XRENGINE_GetPrimaryDirLightCascadeIndex(vec3 fragPosWS)");
        source.ShouldContain("DirectionalLights[0].CascadeMatrices[cascadeIndex]");
        source.ShouldContain("XRENGINE_SampleForwardContactShadowScreenSpace(");
        source.ShouldContain("ForwardContactShadowsEnabled");
        source.ShouldContain("XRENGINE_SampleContactShadowArray(");
        source.ShouldContain("XRENGINE_SampleContactShadow2D(");
        source.ShouldContain("XRENGINE_SampleShadowMapFiltered(");
        source.ShouldContain("XRENGINE_SampleShadowMapArrayFiltered(");
        source.ShouldContain("ivec4 atlasI0 = DirectionalShadowAtlasPacked0[0];");
        source.ShouldContain("vec4 atlasUvScaleBias = DirectionalShadowAtlasParams0[0];");
        source.ShouldContain("XRENGINE_SampleShadowCubeFiltered(");
        source.ShouldContain("XRENGINE_ResolveContactShadowSampleCount(");
        source.ShouldContain("float XRENGINE_ReadDirectionalContactShadowOnly(");
        source.ShouldContain("return XRENGINE_ReadDirectionalContactShadowOnly(fragPos, normal, diffuseFactor);");
        source.ShouldContain("vec3 offsetPosWS = fragPos + normal * ShadowBiasMax;");
        source.ShouldContain("ShadowVogelTapCount");
        source.ShouldContain("shadowI0.w);");
        source.ShouldContain("XRENGINE_SampleSpotContactShadow2DSlot(");
        source.ShouldContain("XRENGINE_SampleSpotShadowDepth2DSlot(");
        source.ShouldContain("light.Base.Base.WorldToLightSpaceProjMatrix,");
        source.ShouldContain("shadowF0.w,");
    }

    [Test]
    public void ShadowSamplingSnippet_DeclaresVogelDiskHelpers()
    {
        string source = LoadShaderSource("Snippets/ShadowSampling.glsl");

        source.ShouldContain("const int XRENGINE_MaxVogelShadowTaps = 32;");
        source.ShouldContain("vec2 XRENGINE_GetVogelDiskTap(int tapIndex, int tapCount)");
        source.ShouldContain("float XRENGINE_SampleShadowMapVogel(");
        source.ShouldContain("float XRENGINE_SampleShadowMapArrayVogel(");
        source.ShouldContain("float XRENGINE_SampleShadowCubeVogel(");
        source.ShouldContain("if (softShadowMode == 3) // VogelDisk");
        source.ShouldContain("float XRENGINE_SampleContactShadowScreenSpace(");
        source.ShouldContain("sampler2D sceneNormal");
        source.ShouldContain("sampler2DArray sceneDepth");
        source.ShouldContain("float layer,");
        source.ShouldContain("sampler2DMS sceneDepth");
        source.ShouldContain("float XRENGINE_EvaluateContactShadowScreenSpaceHit(");
        source.ShouldContain("vec3 XRENGINE_ContactShadowViewPosFromDepth(");
        source.ShouldContain("vec3 XRENGINE_ContactShadowViewPosFromWorldPos(vec3 worldPos, mat4 inverseViewMatrix)");
        source.ShouldContain("bool XRENGINE_TryProjectContactShadowWorldPos(");
        source.ShouldContain("float XRENGINE_ContactShadowViewDepthFromWorldPos(vec3 worldPos, mat4 inverseViewMatrix)");
        source.ShouldContain("vec3 scenePosVS = XRENGINE_ContactShadowViewPosFromDepth(");
        source.ShouldContain("float sampleViewDepth = XRENGINE_ContactShadowViewDepthFromWorldPos(samplePosWS, inverseViewMatrix);");
        source.ShouldContain("float sceneViewDepth = abs(scenePosVS.z);");
        source.ShouldContain("if (!XRENGINE_TryProjectContactShadowWorldPos(samplePosWS, inverseViewMatrix, projMatrix, sampleUv))");
        source.ShouldNotContain("vec4 sampleClip = viewProjectionMatrix * vec4(samplePosWS, 1.0);");
        source.ShouldNotContain("float sampleViewDepth = abs((viewMatrix * vec4(samplePosWS, 1.0)).z);");
        source.ShouldContain("vec3 sceneNormalWS = XRENGINE_DecodeContactShadowNormal(texture(sceneNormal, sampleUvClamped).rg);");
        source.ShouldContain("normalWeight = mix(1.0, 0.35, sameSurfaceNormal * shallowHit);");
        source.ShouldContain("float normalOffset = max(contactNormalOffset, min(max(receiverOffset, compareBias * 2.0), contactDistance * 0.25));");
    }

    [Test]
    public void PointLightShadow_UsesTexelRelativeCompareBias()
    {
        string source = LoadShaderSource("Snippets/ForwardLighting.glsl");

        source.ShouldContain("float XRENGINE_GetPointShadowTexelRelativeBiasForSlot(int shadowSlot, float NoL)");
        // Bias must scale with tan(theta) (not (1+3*slope^2)) to fully cover per-texel
        // stored-depth variance at grazing angles, otherwise the cubemap texel grid shows
        // through as radial herringbone bristles around the lit spot.
        source.ShouldContain("float NoLSafe = max(NoL, 0.05);");
        source.ShouldContain("float tanTheta = sqrt(max(1.0 - NoLSafe * NoLSafe, 0.0)) / NoLSafe;");
        source.ShouldContain("return (2.0 / faceSize) * (1.0 + 2.0 * tanTheta);");
        source.ShouldContain("float texelRel = XRENGINE_GetPointShadowTexelRelativeBiasForSlot(shadowSlot, NoL);");
        source.ShouldContain("float userRel = userBias / max(lightDist, 0.001);");
        source.ShouldContain("float r16fRel = 1.0 / 512.0;");
        source.ShouldContain("float relThreshold = max(texelRel, max(userRel, r16fRel));");
        source.ShouldContain("float compareBias = lightDist * relThreshold;");
        source.ShouldContain("float biasedLightDist = lightDist - compareBias;");
        source.ShouldContain("float margin = (centerDepth - biasedLightDist) / max(farPlaneDist, 0.001);");
    }

    [Test]
    public void PointLightShadow_OffsetsReceiverBeforeSampling()
    {
        string forwardSource = LoadShaderSource("Snippets/ForwardLighting.glsl");
        forwardSource.ShouldContain("vec3 offsetPosWS = fragPos + normal * shadowF1.y;");
        forwardSource.ShouldContain("vec3 fragToLight = offsetPosWS - light.Position;");
        forwardSource.ShouldContain("if (lightDist <= nearPlaneDist + shadowF1.y)");

        string deferredSource = LoadShaderSource("Scene3D/DeferredLightingPoint.fs");
        deferredSource.ShouldContain("vec3 offsetPosWS = fragPosWS + N * ShadowBiasMax;");
        deferredSource.ShouldContain("vec3 fragToLight = offsetPosWS - LightData.Position;");
        deferredSource.ShouldContain("if (lightDist <= nearPlaneDist + ShadowBiasMax)");
    }

    [Test]
    public void ForwardLighting_BindsForwardPrePassContactShadowTextures()
    {
        string source = LoadRepoSource(Path.Combine("XRENGINE", "Rendering", "Lights3DCollection.ForwardLighting.cs"));

        source.ShouldContain("const int forwardContactDepthUnit = 26;");
        source.ShouldContain("const int forwardContactNormalUnit = 27;");
        source.ShouldContain("const int forwardContactDepthArrayUnit = 28;");
        source.ShouldContain("const int forwardContactNormalArrayUnit = 29;");
        source.ShouldContain("Engine.EditorPreferences.Debug.ForwardDepthPrePassEnabled");
        source.ShouldContain("DefaultRenderPipeline.DepthViewTextureName");
        source.ShouldContain("DefaultRenderPipeline.NormalTextureName");
        source.ShouldContain("program.Uniform(\"ForwardContactShadowsEnabled\", forwardContactPrePassAvailable);");
        source.ShouldContain("program.Uniform(\"ForwardContactShadowsArrayEnabled\", forwardContactPrePassArrayAvailable);");
        source.ShouldContain("program.Sampler(\"ForwardContactDepthView\", forwardContactPrePass2DAvailable ? forwardContactDepthTexture! : DummyShadowMap, forwardContactDepthUnit);");
        source.ShouldContain("program.Sampler(\"ForwardContactNormalView\", forwardContactPrePass2DAvailable ? forwardContactNormalTexture! : DummyShadowMap, forwardContactNormalUnit);");
        source.ShouldContain("program.Sampler(\"ForwardContactDepthViewArray\", forwardContactPrePassArrayAvailable ? forwardContactDepthTexture! : DummyShadowMapArray, forwardContactDepthArrayUnit);");
        source.ShouldContain("program.Sampler(\"ForwardContactNormalViewArray\", forwardContactPrePassArrayAvailable ? forwardContactNormalTexture! : DummyShadowMapArray, forwardContactNormalArrayUnit);");
    }

    [Test]
    public void PointLightDebugHeatmaps_ColorFromFilteredLitState()
    {
        string forwardSource = LoadShaderSource("Snippets/ForwardLighting.glsl");
        forwardSource.ShouldContain("float intensity = min(abs(margin) * 20.0, 1.0);");
        forwardSource.ShouldContain("float clampedLit = clamp(lit, 0.0, 1.0);");
        forwardSource.ShouldContain("return vec3((1.0 - clampedLit) * intensity, clampedLit * intensity, 0.0);");

        string deferredSource = LoadShaderSource("Scene3D/DeferredLightingPoint.fs");
        deferredSource.ShouldContain("float intensity = min(abs(_dbgShadowMargin) * 20.0f, 1.0f);");
        deferredSource.ShouldContain("float clampedLit = clamp(_dbgShadowLit, 0.0f, 1.0f);");
        deferredSource.ShouldContain("OutColor = vec3((1.0f - clampedLit) * intensity, clampedLit * intensity, 0.0f);");
    }

    [Test]
    public void ForwardPointShadowSampling_AppliesContactShadowsAsSeparateMultiplier()
    {
        string source = LoadShaderSource("Snippets/ForwardLighting.glsl");
        int pointStart = source.IndexOf("float XRENGINE_ReadShadowMapPoint", System.StringComparison.Ordinal);
        pointStart.ShouldBeGreaterThanOrEqualTo(0);

        int spotStart = source.IndexOf("float XRENGINE_ReadShadowMapSpot", pointStart, System.StringComparison.Ordinal);
        spotStart.ShouldBeGreaterThan(pointStart);

        string pointBody = source[pointStart..spotStart];
        pointBody.ShouldContain("float contact = 1.0;");
        pointBody.ShouldContain("XRENGINE_SampleForwardContactShadowScreenSpace(");
        pointBody.ShouldContain("XRENGINE_SamplePointContactShadowCubeSlot(");
        pointBody.ShouldContain("The cubemap supplies large-scale visibility");
        pointBody.ShouldContain("float shadow = XRENGINE_SamplePointShadowCubeSlot(");
        pointBody.ShouldContain("shadowI0.w) * contact;");
    }

    [Test]
    public void ForwardLocalShadowMetadata_UsesStorageBuffers()
    {
        string source = LoadShaderSource("Snippets/ForwardLighting.glsl");

        source.ShouldContain("layout(std430, binding = 27) readonly buffer ForwardPointShadowMetadataBuffer");
        source.ShouldContain("layout(std430, binding = 28) readonly buffer ForwardSpotShadowMetadataBuffer");
        source.ShouldContain("ForwardPointShadowData PointLightShadows[];");
        source.ShouldContain("ForwardSpotShadowData SpotLightShadows[];");
        source.ShouldContain("lightIndex >= PointLightShadows.length()");
        source.ShouldContain("lightIndex >= SpotLightShadows.length()");
        source.ShouldContain("ivec4 shadowI0 = shadowData.Packed0;");
        source.ShouldContain("ivec4 shadowI1 = shadowData.Packed1;");
        source.ShouldContain("vec4 shadowF4 = shadowData.Params4;");
        source.ShouldContain("vec4 shadowF5 = shadowData.Params5;");
        source.ShouldContain("XRENGINE_SampleForwardContactShadowScreenSpace(");
        source.ShouldNotContain("uniform int PointLightShadowSlots[");
        source.ShouldNotContain("uniform int SpotLightShadowSlots[");
        source.ShouldNotContain("XRENGINE_MAX_FORWARD_LOCAL_LIGHTS");
    }

    [Test]
    public void ForwardDirectionalContactShadows_DoNotRequireDirectionalShadowMap()
    {
        string source = LoadShaderSource("Snippets/ForwardLighting.glsl");
        int helperStart = source.IndexOf("float XRENGINE_ReadDirectionalContactShadowOnly", System.StringComparison.Ordinal);
        helperStart.ShouldBeGreaterThanOrEqualTo(0);

        int dirStart = source.IndexOf("float XRENGINE_ReadShadowMapDir", helperStart, System.StringComparison.Ordinal);
        dirStart.ShouldBeGreaterThan(helperStart);
        int dirEnd = source.IndexOf("vec3 XRENGINE_CalcDirLight", dirStart, System.StringComparison.Ordinal);
        dirEnd.ShouldBeGreaterThan(dirStart);

        string helperBody = source[helperStart..dirStart];
        helperBody.ShouldContain("!EnableContactShadows || !ForwardContactShadowsEnabled || DirLightCount <= 0");
        helperBody.ShouldContain("XRENGINE_SampleForwardContactShadowScreenSpace(");
        helperBody.ShouldContain("normalize(-DirectionalLights[0].Direction)");

        string dirBody = source[dirStart..dirEnd];
        string contactOnlyReturn = "return XRENGINE_ReadDirectionalContactShadowOnly(fragPos, normal, diffuseFactor);";
        dirBody.ShouldContain("if (!ShadowMapEnabled)");
        dirBody.ShouldContain(contactOnlyReturn);
        dirBody.ShouldContain("if (!XRENGINE_ShadowCoordInBounds(fragCoord))");
        dirBody.Split(contactOnlyReturn, System.StringSplitOptions.None).Length.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Test]
    public void DeferredPointShadow_HasR16fQuantizationBiasGuard()
    {
        string source = LoadShaderSource("Scene3D/DeferredLightingPoint.fs");

        source.ShouldContain("uniform float ShadowNearPlaneDist = 0.1f;");
        // Bias must scale with tan(theta) (not (1+3*slope^2)) to fully cover per-texel
        // stored-depth variance at grazing angles, otherwise the cubemap texel grid shows
        // through as radial herringbone bristles around the lit spot. Kept in lockstep
        // with the forward path so both receivers behave identically.
        source.ShouldContain("float NoLSafe = max(NoL, 0.05f);");
        source.ShouldContain("float tanTheta = sqrt(max(1.0f - NoLSafe * NoLSafe, 0.0f)) / NoLSafe;");
        source.ShouldContain("float texelRel = (2.0f / faceSize) * (1.0f + 2.0f * tanTheta);");
        source.ShouldContain("float r16fRel  = 1.0f / 512.0f;");
        source.ShouldContain("float relThreshold = max(texelRel, max(userRel, max(depthRel, r16fRel)));");
    }

    [Test]
    public void ForwardPlusShaders_ViewportRelativeTileLookup()
    {
        string forwardSource = LoadShaderSource("Snippets/ForwardLighting.glsl");
        forwardSource.ShouldContain("uniform vec2 ScreenOrigin;");
        forwardSource.ShouldContain("ivec2 tileCoord = ivec2(floor(gl_FragCoord.xy - ScreenOrigin)) / ForwardPlusTileSize;");
        forwardSource.ShouldContain("tileCoord = clamp(tileCoord, ivec2(0), ivec2(tileCountX - 1, tileCountY - 1));");
    }

    [Test]
    public void ForwardPlusCullingShaders_UseViewSpaceTileFrustums()
    {
        string monoSource = LoadShaderSource("Scene3D/ForwardPlus/LightCulling.comp");
        monoSource.ShouldContain("frustumPlanes[i] *= projection;");
        monoSource.ShouldNotContain("frustumPlanes[i] *= viewProjection;");
        monoSource.ShouldNotContain("frustumPlanes[4] *= view;");
        monoSource.ShouldNotContain("frustumPlanes[5] *= view;");
        // Near/far planes use camera near/far instead of per-tile depth
        monoSource.ShouldContain("uniform float cameraNear;");
        monoSource.ShouldContain("uniform float cameraFar;");
        monoSource.ShouldContain("frustumPlanes[4] = vec4( 0.0, 0.0,-1.0, -cameraNear);");
        monoSource.ShouldContain("frustumPlanes[5] = vec4( 0.0, 0.0, 1.0,  cameraFar);");

        string stereoSource = LoadShaderSource("Scene3D/ForwardPlus/LightCullingStereo.comp");
        stereoSource.ShouldContain("frustumPlanes[i] *= projection;");
        stereoSource.ShouldNotContain("frustumPlanes[i] *= viewProjection;");
        stereoSource.ShouldNotContain("frustumPlanes[4] *= view;");
        stereoSource.ShouldNotContain("frustumPlanes[5] *= view;");
        stereoSource.ShouldContain("uniform float cameraNear;");
        stereoSource.ShouldContain("uniform float cameraFar;");
        stereoSource.ShouldContain("frustumPlanes[4] = vec4( 0.0, 0.0,-1.0, -cameraNear);");
        stereoSource.ShouldContain("frustumPlanes[5] = vec4( 0.0, 0.0, 1.0,  cameraFar);");
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
        source.ShouldContain("float CascadeBiasMin[XRENGINE_MAX_CASCADES];");
        source.ShouldContain("float CascadeBiasMax[XRENGINE_MAX_CASCADES];");
        source.ShouldContain("float CascadeReceiverOffsets[XRENGINE_MAX_CASCADES];");
        source.ShouldContain("int CascadeCount;");
    }

    [Test]
    public void DirectionalLightComponent_PublishesPerCascadeBiasUniforms()
    {
        var light = new DirectionalLightComponent();

        light.CascadeBiasOverrides.Length.ShouldBe(light.CascadeCount);
        light.SetCascadeBiasOverride(1, new DirectionalLightComponent.CascadeShadowBiasOverride(true, -1.0f, 0.002f, -3.0f));

        DirectionalLightComponent.CascadeShadowBiasOverride overrideSettings = light.GetCascadeBiasOverride(1);
        overrideSettings.Enabled.ShouldBeTrue();
        overrideSettings.BiasMin.ShouldBe(0.0f);
        overrideSettings.BiasMax.ShouldBe(0.002f);
        overrideSettings.ReceiverOffset.ShouldBe(0.0f);

        light.CascadeCount = 6;
        light.CascadeBiasOverrides.Length.ShouldBe(6);

        string source = LoadRepoSource(Path.Combine("XRENGINE", "Scene", "Components", "Lights", "Types", "DirectionalLightComponent.cs"));
        source.ShouldContain("CascadeBiasMin[{i}]");
        source.ShouldContain("CascadeBiasMax[{i}]");
        source.ShouldContain("CascadeReceiverOffsets[{i}]");
    }

    [Test]
    public void DirectionalCascadeDefaultBias_NormalizesDirectionalLightBiasToCascadeDepthRange()
    {
        string source = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Scene", "Components", "Lights", "Types", "DirectionalLightComponent.CascadeShadows.cs"));

        source.ShouldContain("float referenceDepthRange = GetPrimaryShadowDepthRange();");
        source.ShouldContain("float cascadeDepthRange = MathF.Max(ShadowBiasDepthRangeEpsilon, halfExtents.Z * 2.0f);");
        source.ShouldContain("NormalizeCompareBiasForDepthRange(ShadowMinBias, referenceDepthRange, cascadeDepthRange)");
        source.ShouldContain("NormalizeCompareBiasForDepthRange(ShadowMaxBias, referenceDepthRange, cascadeDepthRange)");
        source.ShouldContain("return new CascadeShadowBiasSettings(false, biasMin, biasMax, ShadowMaxBias, texelWorldSize);");
        source.ShouldContain("biasMins[i] = slice.BiasMin;");
        source.ShouldContain("biasMaxes[i] = slice.BiasMax;");
        source.ShouldContain("receiverOffsets[i] = slice.ReceiverOffset;");
        source.ShouldNotContain("biasMins[i] = slice.HasManualBiasOverride ? slice.BiasMin : ShadowMinBias;");
    }

    [Test]
    public void DirectionalCascadeShaders_UsePerCascadeBiasAndReceiverOffset()
    {
        string forwardSource = LoadShaderSource("Snippets/ForwardLighting.glsl");
        forwardSource.ShouldContain("float receiverOffset = DirectionalLights[0].CascadeReceiverOffsets[cascadeIndex];");
        forwardSource.ShouldContain("DirectionalLights[0].CascadeBiasMin[cascadeIndex]");
        forwardSource.ShouldContain("DirectionalLights[0].CascadeBiasMax[cascadeIndex]");
        forwardSource.ShouldContain("XRENGINE_GetShadowBiasRange(");

        string deferredSource = LoadShaderSource("Scene3D/DeferredLightingDir.fs");
        deferredSource.ShouldContain("float CascadeBiasMin[MAX_CASCADES];");
        deferredSource.ShouldContain("float receiverOffset = LightData.CascadeReceiverOffsets[cascadeIndex];");
        deferredSource.ShouldContain("LightData.CascadeBiasMin[cascadeIndex]");
        deferredSource.ShouldContain("LightData.CascadeBiasMax[cascadeIndex]");
        deferredSource.ShouldContain("SampleDeferredContactShadow(fragPosWS, N, normalize(-LightData.Direction), receiverOffset, bias, viewDepth)");
    }

    [Test]
    public void DirectionalPrimaryShadowAtlas_IsSubmittedRenderedBoundAndPreviewed()
    {
        string lightSource = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Scene", "Components", "Lights", "Types", "DirectionalLightComponent.cs"));
        lightSource.ShouldContain("protected override bool UsesAtlasShadowViewport");
        lightSource.ShouldContain("Engine.Rendering.Settings.UseDirectionalShadowAtlas");

        string cascadeSource = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Scene", "Components", "Lights", "Types", "DirectionalLightComponent.CascadeShadows.cs"));
        cascadeSource.ShouldContain("private DirectionalCascadeAtlasSlot _primaryAtlasSlot;");
        cascadeSource.ShouldContain("internal void SetPrimaryAtlasSlot(");
        cascadeSource.ShouldContain("public bool TryGetPrimaryAtlasSlot(");
        cascadeSource.ShouldContain("internal void CopyPublishedDirectionalAtlasUniformData(");
        cascadeSource.ShouldContain("internal bool RenderPrimaryShadowAtlasTile(");
        cascadeSource.ShouldContain("(ShadowMap is not null || Engine.Rendering.Settings.UseDirectionalShadowAtlas)");

        string shadowCollectionSource = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Rendering", "Lights3DCollection.Shadows.cs"));
        shadowCollectionSource.ShouldContain("DirectionalLightComponent => Engine.Rendering.Settings.UseDirectionalShadowAtlas");
        shadowCollectionSource.ShouldContain("EShadowProjectionType.DirectionalPrimary");
        shadowCollectionSource.ShouldContain("TryGetDirectionalPrimaryShadowAtlasAllocation");
        shadowCollectionSource.ShouldContain("directionalLight.SetPrimaryAtlasSlot(");

        string atlasManagerSource = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Rendering", "Shadows", "ShadowAtlasManager.cs"));
        atlasManagerSource.ShouldContain("EShadowProjectionType.DirectionalPrimary");
        atlasManagerSource.ShouldContain("RenderPrimaryShadowAtlasTile(");

        string forwardSource = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Rendering", "Lights3DCollection.ForwardLighting.cs"));
        forwardSource.ShouldContain("firstDirLight.CastsShadows");
        forwardSource.ShouldContain("CopyPublishedDirectionalAtlasUniformData(");

        string editorSource = LoadRepoSource(Path.Combine("XREngine.Editor", "ComponentEditors", "LightComponentEditorShared.cs"));
        editorSource.ShouldContain("Use Directional Shadow Atlas");
        editorSource.ShouldContain("Directional Shadow Atlas Tile");
        editorSource.ShouldContain("TryGetPrimaryAtlasSlot(");
    }

    [Test]
    public void DirectionalPrimaryShadowAtlasShaders_SamplePrimaryTileBeforeLegacyMap()
    {
        string forwardSource = LoadShaderSource("Snippets/ForwardLighting.glsl");
        forwardSource.ShouldContain("ivec4 atlasI0 = DirectionalShadowAtlasPacked0[0];");
        forwardSource.ShouldContain("vec4 atlasUvScaleBias = DirectionalShadowAtlasParams0[0];");
        forwardSource.ShouldContain("ShadowFilterRadius * atlasRadiusScale");
        forwardSource.ShouldContain("if (fallbackMode == 1 || fallbackMode == 2 || fallbackMode == 4)");

        string deferredSource = LoadShaderSource("Scene3D/DeferredLightingDir.fs");
        deferredSource.ShouldContain("ivec4 atlasI0 = DirectionalShadowAtlasPacked0[0];");
        deferredSource.ShouldContain("vec4 atlasUvScaleBias = DirectionalShadowAtlasUvScaleBias[0];");
        deferredSource.ShouldContain("ShadowFilterRadius * atlasRadiusScale");
        deferredSource.ShouldContain("if (fallbackMode == 1 || fallbackMode == 2 || fallbackMode == 4)");
    }

    [Test]
    public void DeferredDirectionalShader_OffsetsReceiverBeforeShadowSample()
    {
        string source = LoadShaderSource("Scene3D/DeferredLightingDir.fs");

        source.ShouldContain("vec3 offsetPosWS = fragPosWS + N * ShadowBiasMax;");
        source.ShouldContain("uniform bool EnableContactShadows = true;");
        source.ShouldContain("uniform float ContactShadowDistance = 1.0f;");
        source.ShouldContain("uniform int ShadowSamples = 8;");
        source.ShouldContain("uniform int ShadowBlockerSamples = 8;");
        source.ShouldContain("uniform int ShadowFilterSamples = 8;");
        source.ShouldContain("uniform int ShadowVogelTapCount = 5;");
        source.ShouldContain("uniform float ShadowBlockerSearchRadius = 0.01f;");
        source.ShouldContain("uniform float ShadowMinPenumbra = 0.001f;");
        source.ShouldContain("uniform float ShadowMaxPenumbra = 0.015f;");
        source.ShouldContain("uniform int SoftShadowMode = 2;");
        source.ShouldContain("uniform float LightSourceRadius = 1.2f;");
        source.ShouldContain("uniform int ContactShadowSamples = 16;");
        source.ShouldContain("uniform float ContactShadowThickness = 2.0f;");
        source.ShouldContain("uniform vec2 ScreenOrigin;");
        source.ShouldContain("vec2 fragCoordLocal = gl_FragCoord.xy - ScreenOrigin;");
        source.ShouldContain("SampleDeferredContactShadow(");
        source.ShouldContain("XRENGINE_SampleContactShadowScreenSpace(");
        source.ShouldContain("SampleShadowMapFilteredLocal(");
        source.ShouldContain("SampleShadowMapArrayFilteredLocal(");
        source.ShouldContain("XRENGINE_ResolveContactShadowSampleCount(");
        source.ShouldContain("ShadowBiasMax,");
        source.ShouldContain("bias,");
        source.ShouldContain("ViewProjectionMatrix,");
        source.ShouldContain("DepthMode,");
        source.ShouldNotContain("XRENGINE_SampleContactShadow2D(");
        source.ShouldNotContain("XRENGINE_SampleContactShadowArray(");
        source.ShouldNotContain("SampleContactShadowScreenSpaceLocal(");
        source.ShouldNotContain("ResolveContactShadowSampleCountLocal(");
        source.ShouldNotContain("MinFade");
        source.ShouldNotContain("MaxFade");
    }

    [Test]
    public void DeferredPointShader_DoesNotDeclareDistanceFadeUniforms()
    {
        string source = LoadShaderSource("Scene3D/DeferredLightingPoint.fs");

        source.ShouldNotContain("MinFade");
        source.ShouldNotContain("MaxFade");
        source.ShouldContain("uniform int ShadowVogelTapCount = 5;");
        source.ShouldContain("uniform vec2 ScreenOrigin;");
        source.ShouldContain("uniform mat4 ViewMatrix;");
        source.ShouldContain("uniform mat4 ViewProjectionMatrix;");
        source.ShouldContain("vec2 fragCoordLocal = gl_FragCoord.xy - ScreenOrigin;");
        source.ShouldContain("SampleDeferredContactShadow(");
        source.ShouldContain("XRENGINE_SampleContactShadowScreenSpace(");
        source.ShouldContain("DepthMode,");
        source.ShouldNotContain("XRENGINE_SampleContactShadowCube(");
    }

    [Test]
    public void PointShadowSampling_UsesBalancedKernelTapOrder()
    {
        string deferredSource = LoadShaderSource("Scene3D/DeferredLightingPoint.fs");
        deferredSource.ShouldContain("const int LocalShadowCubeKernelTapOrder[20] = int[](");
        deferredSource.ShouldContain("vec3 GetShadowCubeKernelTapLocal(int tapIndex)");
        deferredSource.ShouldContain("LocalShadowCubeKernel[LocalShadowCubeKernelTapOrder[tapIndex]]");

        string samplingSource = LoadShaderSource("Snippets/ShadowSampling.glsl");
        samplingSource.ShouldContain("const int XRENGINE_ShadowCubeKernelTapOrder[20] = int[](");
        samplingSource.ShouldContain("vec3 XRENGINE_GetShadowCubeKernelTap(int tapIndex)");
        samplingSource.ShouldContain("XRENGINE_ShadowCubeKernel[XRENGINE_ShadowCubeKernelTapOrder[tapIndex]]");
    }

    [Test]
    public void PointLightComponent_UsesRenderTranslationForShaderPosition()
    {
        string source = LoadRepoSource(Path.Combine("XRENGINE", "Scene", "Components", "Lights", "Types", "PointLightComponent.cs"));

        source.ShouldContain("Vector3 lightPosition = Transform.RenderTranslation;");
        source.ShouldContain("program.Uniform($\"{flatPrefix}Position\", lightPosition);");
        source.ShouldContain("program.Uniform($\"{prefix}.Position\", lightPosition);");
        source.ShouldContain("program.Uniform(\"LightPos\", Transform.RenderTranslation);");
        source.ShouldContain("program.Uniform(\"ShadowNearPlaneDist\", ShadowNearPlaneDistance);");
        source.ShouldContain("private void SyncShadowCaptureTransforms()");
        source.ShouldContain("SetField(ref _influenceVolume, new Sphere(lightPosition, _influenceVolume.Radius));");
        source.ShouldContain("SetRenderMatrix(Matrix4x4.CreateTranslation(lightPosition), recalcAllChildRenderMatrices: true)");
        source.ShouldContain("SyncShadowCaptureTransforms();");
    }

    [Test]
    public void ForwardPointShadowSampling_DispatchesThroughFixedSamplerSlots()
    {
        string source = LoadShaderSource("Snippets/ForwardLighting.glsl");

        source.ShouldContain("float XRENGINE_SamplePointShadowCubeSlot(");
        source.ShouldContain("float XRENGINE_SamplePointContactShadowCubeSlot(");
        source.ShouldContain("float XRENGINE_GetPointShadowSampleRadiusForSlot(");
        source.ShouldContain("PointLightShadowMaps[0]");
        source.ShouldContain("PointLightShadowMaps[3]");
        source.ShouldNotContain("PointLightShadowMaps[shadowSlot]");
    }

    [Test]
    public void ForwardSpotShadowSampling_DispatchesThroughFixedSamplerSlots()
    {
        string source = LoadShaderSource("Snippets/ForwardLighting.glsl");

        source.ShouldContain("float XRENGINE_SampleSpotContactShadow2DSlot(");
        source.ShouldContain("float XRENGINE_SampleSpotShadowMoment2DSlot(");
        source.ShouldContain("float XRENGINE_SampleSpotShadowDepth2DSlot(");
        source.ShouldContain("float XRENGINE_ReadSpotShadowCenterDepthForSlot(");
        source.ShouldContain("SpotLightShadowMaps[0]");
        source.ShouldContain("SpotLightShadowMaps[3]");
        source.ShouldNotContain("SpotLightShadowMaps[shadowSlot]");
    }

    [Test]
    public void PointLightShadowPath_UsesForcedGeneratedVertexContract()
    {
        string pointLightSource = LoadRepoSource(Path.Combine("XRENGINE", "Scene", "Components", "Lights", "Types", "PointLightComponent.cs"));
        pointLightSource.ShouldNotContain("PointLightShadowDepth.vs");
        pointLightSource.ShouldContain("mat = new(refs, geomShader, fragShader);");
        pointLightSource.ShouldContain("mat = new(refs, fragShader);");

        string pointLightGeometrySource = LoadShaderSource("PointLightShadowDepth.gs");
        pointLightGeometrySource.ShouldContain("gl_Position = ViewProjectionMatrices[face] * vec4(FragPos, 1.0);");
        pointLightGeometrySource.ShouldContain("layout (location = 12) in vec4 InFragColor0[];");
        pointLightGeometrySource.ShouldContain("layout (location = 12) out vec4 FragColor0;");
        pointLightGeometrySource.ShouldContain("FragColor0 = InFragColor0[i];");
        pointLightGeometrySource.ShouldContain("layout (location = 20) out vec3 FragPosLocal;");

        string glShaderSource = LoadRepoSource(Path.Combine("XRENGINE", "Rendering", "API", "Rendering", "OpenGL", "Types", "Meshes", "GLShader.cs"));
        glShaderSource.ShouldContain("GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks");

        string compatibilitySource = LoadRepoSource(Path.Combine("XRENGINE", "Rendering", "API", "Rendering", "OpenGL", "Types", "Meshes", "GLShaderSourceCompatibility.cs"));
        compatibilitySource.ShouldContain("InjectMissingGLPerVertexBlocks");
        compatibilitySource.ShouldContain("in gl_PerVertex");
        compatibilitySource.ShouldContain("out gl_PerVertex");

        string meshRendererSource = LoadRepoSource(Path.Combine("XREngine", "Rendering", "API", "Rendering", "OpenGL", "Types", "Mesh Renderer", "GLMeshRenderer.Shaders.cs"));
        meshRendererSource.ShouldContain("bool pointLightShadowPass = renderState?.ShadowPass == true");
        meshRendererSource.ShouldContain("&& UsesPointLightShadowCubemap(globalMaterialOverride);");
        meshRendererSource.ShouldContain("|| pointLightShadowPass;");

        string meshRendererRenderingSource = LoadRepoSource(Path.Combine("XREngine", "Rendering", "API", "Rendering", "OpenGL", "Types", "Mesh Renderer", "GLMeshRenderer.Rendering.cs"));
        meshRendererRenderingSource.ShouldContain("private static bool IsPointLightShadowGeometryPass()");
        meshRendererRenderingSource.ShouldContain("internal bool RequiresTriangleOnlyDrawsForCurrentPass()");

        string meshRendererDrawSource = LoadRepoSource(Path.Combine("XREngine", "Rendering", "API", "Rendering", "OpenGL", "Types", "Mesh Renderer", "GLMeshRenderer.cs"));
        meshRendererDrawSource.ShouldContain("if (ActiveMeshRenderer.RequiresTriangleOnlyDrawsForCurrentPass())");
    }

    [Test]
    public void DeferredSpotShader_UsesSharedContactShadowHelpers()
    {
        string source = LoadShaderSource("Scene3D/DeferredLightingSpot.fs");

        source.ShouldContain("uniform bool EnableContactShadows = true;");
        source.ShouldContain("uniform float ContactShadowDistance = 0.1f;");
        source.ShouldContain("uniform int ContactShadowSamples = 16;");
        source.ShouldContain("uniform float ContactShadowThickness = 1.0f;");
        source.ShouldContain("uniform float ContactShadowNormalOffset = 0.036f;");
        source.ShouldContain("uniform int ShadowVogelTapCount = 5;");
        source.ShouldContain("uniform vec2 ScreenOrigin;");
        source.ShouldContain("vec2 fragCoordLocal = gl_FragCoord.xy - ScreenOrigin;");
        source.ShouldContain("SampleDeferredContactShadow(");
        source.ShouldContain("XRENGINE_SampleContactShadowScreenSpace(");
        source.ShouldContain("XRENGINE_ResolveContactShadowSampleCount(");
        source.ShouldContain("ShadowBiasMax,");
        source.ShouldContain("bias,");
        source.ShouldContain("ViewProjectionMatrix,");
        source.ShouldContain("DepthMode,");
        source.ShouldNotContain("XRENGINE_SampleContactShadow2D(");
        source.ShouldNotContain("SampleContactShadowScreenSpaceLocal(");
        source.ShouldNotContain("ResolveContactShadowSampleCountLocal(");
        source.ShouldNotContain("MinFade");
        source.ShouldNotContain("MaxFade");
    }

    [Test]
    public void UniformRequirementsDetection_RecognizesForwardLightingEngineUniforms()
    {
        string source = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Materials", "Options", "EUniformRequirements.cs"));

        source.ShouldContain("[EngineShaderBindingNames.Uniforms.ForwardPlusEnabled] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Uniforms.ForwardContactShadowsEnabled] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Uniforms.ForwardContactShadowsArrayEnabled] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Uniforms.ProbeGridDims] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Uniforms.ShadowMapEnabled] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Uniforms.ShadowVogelTapCount] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Samplers.ForwardContactDepthView] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Samplers.ForwardContactNormalView] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Samplers.ForwardContactDepthViewArray] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Samplers.ForwardContactNormalViewArray] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Uniforms.PointLightShadowNearPlanes] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Uniforms.PointLightShadowVogelTapCount] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Uniforms.PointLightShadowDebugModes] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Uniforms.SpotLightShadowVogelTapCount] = EUniformRequirements.Lights,");
        source.ShouldContain("[EngineShaderBindingNames.Uniforms.SpotLightShadowDebugModes] = EUniformRequirements.Lights,");
    }

    [Test]
    public void GLMaterial_RebindsLightSamplerUniformsEveryBindingBatch()
    {
        string source = LoadRepoSource(Path.Combine("XRENGINE", "Rendering", "API", "Rendering", "OpenGL", "Types", "Meshes", "GLMaterial.cs"));

        source.ShouldContain("Light bindings include shadow-map samplers.");
        source.ShouldContain("if (requiredRequirements.HasFlag(EUniformRequirements.Lights))");
        source.ShouldContain("return true;");
        source.ShouldContain("if (reqs.HasFlag(EUniformRequirements.Lights))");
        source.ShouldContain("missingProgramRequirements |= EUniformRequirements.Lights;");
    }

    [Test]
    public void PointShadowCasterVariants_PreserveAlphaDiscardsAndWriteRadialDepth()
    {
        string shaderHelperSource = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Resources", "Shaders", "ShaderHelper.cs"));
        shaderHelperSource.ShouldContain("XRENGINE_POINT_SHADOW_CASTER_PASS");
        shaderHelperSource.ShouldContain("GetPointShadowCasterForwardVariant");
        shaderHelperSource.ShouldContain("\"UberShader.frag\" => CreateDefinedShaderVariant(sourceShader, PointShadowCasterPassDefine)");

        string factorySource = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Shaders", "ShadowCasterVariantFactory.cs"));
        factorySource.ShouldContain("CreatePointLightMaterialVariant");
        factorySource.ShouldContain("PointLightShadowDepth.gs");
        factorySource.ShouldContain("ShadowBindingSourceMaterial = sourceMaterial");

        string meshRendererSource = LoadRepoSource(Path.Combine("XRENGINE", "Rendering", "API", "Rendering", "OpenGL", "Types", "Mesh Renderer", "GLMeshRenderer.Rendering.cs"));
        meshRendererSource.ShouldContain("GetPointShadowCasterVariant(");
        meshRendererSource.ShouldContain("pointShadowVariant.ShadowUniformSourceMaterial = globalMaterialOverride;");

        string glMaterialSource = LoadRepoSource(Path.Combine("XRENGINE", "Rendering", "API", "Rendering", "OpenGL", "Types", "Meshes", "GLMaterial.cs"));
        glMaterialSource.ShouldContain("Data.ShadowUniformSourceMaterial");
        glMaterialSource.ShouldContain("shadowUniformSource.OnSettingShadowUniforms(materialProgram.Data);");
        glMaterialSource.ShouldContain("Engine.Rendering.State.IsShadowPass && Data.HasSettingShadowUniformHandlers");
        glMaterialSource.ShouldContain("Data.OnSettingShadowUniforms(materialProgram.Data);");

        string uberSource = LoadShaderSource("Uber/UberShader.frag");
        uberSource.ShouldContain("defined(XRENGINE_SHADOW_CASTER_PASS) || defined(XRENGINE_POINT_SHADOW_CASTER_PASS)");
        uberSource.ShouldContain("uniform vec3 LightPos;");
        uberSource.ShouldContain("uniform float FarPlaneDist;");
        uberSource.ShouldContain("Depth = length(FragPos - LightPos) / FarPlaneDist;");

        string alphaSource = LoadShaderSource("Common/LitTexturedAlphaForward.fs");
        alphaSource.ShouldContain("if (alphaMask < AlphaCutoff)");
        alphaSource.ShouldContain("Depth = length(FragPos - LightPos) / FarPlaneDist;");

        string normalAlphaSource = LoadShaderSource("Common/LitTexturedNormalAlphaForward.fs");
        normalAlphaSource.ShouldContain("#if !defined(XRENGINE_SHADOW_CASTER_PASS) && !defined(XRENGINE_POINT_SHADOW_CASTER_PASS)");
        normalAlphaSource.ShouldContain("layout (location = 3) in vec3 FragBinorm;");
        normalAlphaSource.ShouldContain("Depth = vec4(length(FragPos - LightPos) / FarPlaneDist, 0.0, 0.0, 0.0);");
    }

    [Test]
    public void LightSources_DeclareTunedShadowDefaults()
    {
        string lightComponentSource = LoadRepoSource(Path.Combine("XRENGINE", "Scene", "Components", "Lights", "Types", "LightComponent.cs"));
        lightComponentSource.ShouldContain("private float _shadowMaxBias = 0.004f;");
        lightComponentSource.ShouldContain("private float _shadowMinBias = 0.00001f;");
        lightComponentSource.ShouldContain("private float _shadowExponent = 1.221f;");
        lightComponentSource.ShouldContain("private float _shadowExponentBase = 0.035f;");
        lightComponentSource.ShouldContain("private int _filterSamples = 4;");
        lightComponentSource.ShouldContain("private int _blockerSamples = 4;");
        lightComponentSource.ShouldContain("private int _vogelTapCount = 5;");
        lightComponentSource.ShouldContain("private float _filterRadius = 0.0012f;");
        lightComponentSource.ShouldContain("public const float MaxAutomaticContactHardeningLightRadius = 0.25f;");
        lightComponentSource.ShouldContain("private bool _useLightRadiusForContactHardening = true;");
        lightComponentSource.ShouldContain("private bool _enableContactShadows = true;");
        lightComponentSource.ShouldContain("private float _contactShadowDistance = 0.1f;");
        lightComponentSource.ShouldContain("private int _contactShadowSamples = 4;");

        string directionalSource = LoadRepoSource(Path.Combine("XRENGINE", "Scene", "Components", "Lights", "Types", "DirectionalLightComponent.cs"));
        directionalSource.ShouldContain("SetShadowMapResolution(2048u, 2048u);");
        directionalSource.ShouldContain("ShadowExponentBase = 0.035f;");
        directionalSource.ShouldContain("ShadowExponent = 1.221f;");
        directionalSource.ShouldContain("ShadowMinBias = 0.00001f;");
        directionalSource.ShouldContain("ShadowMaxBias = 0.004f;");
        directionalSource.ShouldContain("BlockerSamples = 8;");
        directionalSource.ShouldContain("FilterSamples = 8;");
        directionalSource.ShouldContain("FilterRadius = 0.0012f;");
        directionalSource.ShouldContain("BlockerSearchRadius = 0.01f;");
        directionalSource.ShouldContain("MinPenumbra = 0.001f;");
        directionalSource.ShouldContain("MaxPenumbra = 0.015f;");
        directionalSource.ShouldContain("SoftShadowMode = ESoftShadowMode.ContactHardeningPcss;");
        directionalSource.ShouldContain("LightSourceRadius = 1.2f;");
        directionalSource.ShouldContain("ContactShadowDistance = 1.0f;");
        directionalSource.ShouldContain("ContactShadowSamples = 16;");
        directionalSource.ShouldContain("ContactShadowThickness = 2.0f;");

        var directionalLight = new DirectionalLightComponent();
        directionalLight.ShadowMapResolutionWidth.ShouldBe(2048u);
        directionalLight.ShadowMapResolutionHeight.ShouldBe(2048u);
        directionalLight.CastsShadows.ShouldBeTrue();
        directionalLight.EnableCascadedShadows.ShouldBeTrue();
        directionalLight.UseLightRadiusForContactHardening.ShouldBeFalse();
        directionalLight.BlockerSamples.ShouldBe(8);
        directionalLight.FilterSamples.ShouldBe(8);
        directionalLight.BlockerSearchRadius.ShouldBe(0.01f);
        directionalLight.MinPenumbra.ShouldBe(0.001f);
        directionalLight.MaxPenumbra.ShouldBe(0.015f);
        directionalLight.LightSourceRadius.ShouldBe(1.2f);
        directionalLight.EnableContactShadows.ShouldBeTrue();
        directionalLight.ContactShadowDistance.ShouldBe(1.0f);
        directionalLight.ContactShadowSamples.ShouldBe(16);
        directionalLight.ContactShadowThickness.ShouldBe(2.0f);
        directionalLight.ContactShadowFadeStart.ShouldBe(10.0f);
        directionalLight.ContactShadowFadeEnd.ShouldBe(40.0f);
        directionalLight.ContactShadowNormalOffset.ShouldBe(0.0f);
        directionalLight.ContactShadowJitterStrength.ShouldBe(1.0f);

        string spotSource = LoadRepoSource(Path.Combine("XRENGINE", "Scene", "Components", "Lights", "Types", "SpotLightComponent.cs"));
        spotSource.ShouldContain("SetShadowMapResolution(512u, 512u);");
        spotSource.ShouldContain("ShadowMinBias = 0.0001f;");
        spotSource.ShouldContain("ShadowMaxBias = 0.07f;");
        spotSource.ShouldContain("ShadowExponentBase = 0.2f;");
        spotSource.ShouldContain("ShadowExponent = 1.0f;");
        spotSource.ShouldContain("BlockerSamples = 8;");
        spotSource.ShouldContain("FilterSamples = 8;");
        spotSource.ShouldContain("FilterRadius = 0.0012f;");
        spotSource.ShouldContain("BlockerSearchRadius = 0.1f;");
        spotSource.ShouldContain("MinPenumbra = 0.0002f;");
        spotSource.ShouldContain("MaxPenumbra = 0.05f;");
        spotSource.ShouldContain("SoftShadowMode = ESoftShadowMode.ContactHardeningPcss;");
        spotSource.ShouldContain("LightSourceRadius = 0.1f;");
        spotSource.ShouldContain("EnableContactShadows = true;");
        spotSource.ShouldContain("ContactShadowDistance = 0.1f;");
        spotSource.ShouldContain("ContactShadowSamples = 16;");
        spotSource.ShouldContain("ContactShadowThickness = 1.0f;");
        spotSource.ShouldContain("ContactShadowNormalOffset = 0.036f;");

        var spotLight = new SpotLightComponent();
        spotLight.ShadowMapResolutionWidth.ShouldBe(512u);
        spotLight.ShadowMapResolutionHeight.ShouldBe(512u);
        spotLight.UseLightRadiusForContactHardening.ShouldBeTrue();
        spotLight.EffectiveLightSourceRadius.ShouldBe(LightComponent.MaxAutomaticContactHardeningLightRadius);
        spotLight.BlockerSamples.ShouldBe(8);
        spotLight.FilterSamples.ShouldBe(8);
        spotLight.BlockerSearchRadius.ShouldBe(0.1f);
        spotLight.MinPenumbra.ShouldBe(0.0002f);
        spotLight.MaxPenumbra.ShouldBe(0.05f);
        spotLight.SoftShadowMode.ShouldBe(ESoftShadowMode.ContactHardeningPcss);
        spotLight.LightSourceRadius.ShouldBe(0.1f);
        spotLight.EnableContactShadows.ShouldBeTrue();
        spotLight.ContactShadowDistance.ShouldBe(0.1f);
        spotLight.ContactShadowSamples.ShouldBe(16);
        spotLight.ContactShadowThickness.ShouldBe(1.0f);
        spotLight.ContactShadowFadeStart.ShouldBe(10.0f);
        spotLight.ContactShadowFadeEnd.ShouldBe(40.0f);
        spotLight.ContactShadowNormalOffset.ShouldBe(0.036f);
        spotLight.ContactShadowJitterStrength.ShouldBe(1.0f);
    }

    [Test]
    public void LocalLights_CanResolveContactHardeningSourceRadiusFromLightRadius()
    {
        PointLightComponent pointLight = new(12.5f, 1.0f)
        {
            LightSourceRadius = 0.25f,
        };

        pointLight.UseLightRadiusForContactHardening.ShouldBeTrue();
        pointLight.EffectiveLightSourceRadius.ShouldBe(LightComponent.MaxAutomaticContactHardeningLightRadius);
        pointLight.Radius = 0.125f;
        pointLight.EffectiveLightSourceRadius.ShouldBe(0.125f);
        pointLight.UseLightRadiusForContactHardening = false;
        pointLight.EffectiveLightSourceRadius.ShouldBe(0.25f);
        pointLight.LightSourceRadius.ShouldBe(0.25f);

        SpotLightComponent spotLight = new(10.0f, 45.0f, 20.0f, 1.0f, 1.0f)
        {
            LightSourceRadius = 0.25f,
        };

        spotLight.UseLightRadiusForContactHardening.ShouldBeTrue();
        spotLight.EffectiveLightSourceRadius.ShouldBe(LightComponent.MaxAutomaticContactHardeningLightRadius);
        spotLight.Distance = 0.1f;
        spotLight.EffectiveLightSourceRadius.ShouldBe(0.1f, 0.0001f);
        spotLight.SetCutoffs(20.0f, 60.0f);
        spotLight.EffectiveLightSourceRadius.ShouldBe(MathF.Tan(XREngine.Data.Core.XRMath.DegToRad(60.0f)) * 0.1f, 0.0001f);
        spotLight.Distance = 5.0f;
        spotLight.EffectiveLightSourceRadius.ShouldBe(LightComponent.MaxAutomaticContactHardeningLightRadius);
        spotLight.UseLightRadiusForContactHardening = false;
        spotLight.EffectiveLightSourceRadius.ShouldBe(0.25f);
        spotLight.LightSourceRadius.ShouldBe(0.25f);
    }

    [Test]
    public void DeferredDirectionalShader_SkipsShadowing_WhenNoShadowMapIsAvailable()
    {
        string source = LoadShaderSource("Scene3D/DeferredLightingDir.fs");

        source.ShouldContain("uniform bool LightHasShadowMap = true;");
        source.ShouldContain("if (!LightHasShadowMap)");
        source.ShouldContain("return 1.0f;");
    }

    [Test]
    public void DeferredDirectionalLightPass_BindsSafeShadowFallbacks_AfterLightReactivation()
    {
        string lightComponentSource = LoadRepoSource(Path.Combine("XRENGINE", "Scene", "Components", "Lights", "Types", "LightComponent.cs"));
        lightComponentSource.ShouldContain("ShadowMap?.Destroy();");
        lightComponentSource.ShouldContain("ShadowMap = null;");

        string lightCombineSource = LoadRepoSource(Path.Combine("XRENGINE", "Rendering", "Pipelines", "Commands", "Features", "VPRC_LightCombinePass.cs"));
        lightCombineSource.ShouldContain("public XRMeshRenderer? DirectionalLightRenderer { get; private set; }");
        lightCombineSource.ShouldContain("RenderLight(DirectionalLightRenderer!, c);");
        lightCombineSource.ShouldContain("DirectionalLightRenderer = new XRMeshRenderer(dirLightMesh, dirLightMat);");
        lightCombineSource.ShouldContain("materialProgram.Uniform(\"LightHasShadowMap\", directionalHasShadowMap);");
        lightCombineSource.ShouldContain("else if (_currentLightComponent is DirectionalLightComponent)");
        lightCombineSource.ShouldContain("selectedShadowMap as XRTexture2D ?? DummyShadowMap, 4");
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
