using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SecondaryPassShaderContractTests
{
    [Test]
    public void GrabpassGaussian_UsesPreweightedFixedTapKernel()
    {
        string source = LoadShaderSource(Path.Combine("UI", "GrabpassGaussian.frag"));

        source.ShouldContain("const int MaxBlurTaps = 12;");
        source.ShouldContain("const vec2 BlurTapOffsets[MaxBlurTaps] = vec2[](");
        source.ShouldContain("const float BlurTapWeights[MaxBlurTaps] = float[](");
        source.ShouldContain("#pragma snippet \"ScreenSpaceUtils\"");
        source.ShouldContain("vec2 uv = XRENGINE_ScreenUV(gl_FragCoord.xy, vec2(ScreenWidth, ScreenHeight));");
        source.ShouldContain("int requested = clamp(SampleCount, 0, MaxBlurTaps);");
        source.ShouldContain("int activeTapCount = requested >= 12 ? 12 : (requested >= 8 ? 8 : (requested >= 4 ? 4 : 0));");
        source.ShouldContain("vec2 blurStep = texelSize * BlurStrength;");
        source.ShouldNotContain("gaussian(");
        source.ShouldNotContain("sqrt(");
        source.ShouldNotContain("cos(");
        source.ShouldNotContain("sin(");
    }

    [Test]
    public void FullscreenTri_UsesAttributeLessVertexIdPath()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "FullscreenTri.vs"));

        source.ShouldContain("gl_VertexID");
        source.ShouldNotContain("gl_VertexIndex");
        source.ShouldNotContain("#ifdef XRENGINE_VULKAN");
        source.ShouldNotContain("layout(location = 0) in");
        source.ShouldNotContain("in vec3 Position");
        source.ShouldNotContain("Position.xy");
        source.ShouldContain("layout(location = 0) out vec3 FragPos;");
    }

    [Test]
    public void UITextBatched_DoesNotForceMagentaBeforeDebugMode()
    {
        string source = LoadShaderSource(Path.Combine("Common", "UITextBatched.fs"));
        int mainIndex = source.IndexOf("void main()", StringComparison.Ordinal);
        mainIndex.ShouldBeGreaterThanOrEqualTo(0);

        int debugModeIndex = source.IndexOf("if (TextDebugMode == 1)", mainIndex, StringComparison.Ordinal);
        debugModeIndex.ShouldBeGreaterThan(mainIndex);

        string preDebugModeBody = source[mainIndex..debugModeIndex];
        preDebugModeBody.ShouldNotContain("FragColor = vec4(1.0, 0.0, 1.0, 1.0);");
        preDebugModeBody.ShouldNotContain("return;");
    }

    [Test]
    public void XRQuadFrameBuffer_AttachesFullscreenTriVertexShaderByDefault()
    {
        string source = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Objects",
            "Render Targets",
            "XRQuadFrameBuffer.cs"));

        source.ShouldContain("mat.VertexShaders.Count == 0");
        source.ShouldContain("FullscreenTri.vs");
    }

    [Test]
    public void VulkanShaderFixups_RewriteOpenGlVertexIdBuiltIn()
    {
        string source = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "Shaders",
            "VulkanShaderSourceFixups.cs"));

        source.ShouldContain("Replace(\"gl_VertexID\", \"gl_VertexIndex\"");
    }

    [Test]
    public void TransformToolGizmoShaders_RequestViewportAndClipPolicyUniforms()
    {
        string gizmoLine = LoadShaderSource(Path.Combine("Common", "GizmoLine.gs"));
        string gizmoArrowHead = LoadShaderSource(Path.Combine("Common", "GizmoArrowHead.gs"));
        string transformTool = ReadWorkspaceFile(Path.Combine(
            "XREngine",
            "Scene",
            "Components",
            "Editing",
            "TransformTool3D.cs"));

        gizmoLine.ShouldContain("uniform float ScreenWidth;");
        gizmoLine.ShouldContain("uniform float ScreenHeight;");
        gizmoLine.ShouldContain("uniform int ClipDepthRange;");
        gizmoLine.ShouldContain("ClipDepthRange == 1 ? a.z + a.w : a.z");
        gizmoLine.ShouldContain("ClipDepthRange == 1 ? b.z + b.w : b.z");

        gizmoArrowHead.ShouldContain("uniform float ScreenWidth;");
        gizmoArrowHead.ShouldContain("uniform float ScreenHeight;");
        gizmoArrowHead.ShouldContain("uniform int ClipDepthRange;");
        gizmoArrowHead.ShouldContain("ClipDepthRange == 1 ? a.z + a.w : a.z");
        gizmoArrowHead.ShouldContain("ClipDepthRange == 1 ? b.z + b.w : b.z");

        transformTool.ShouldContain("EUniformRequirements.Camera |");
        transformTool.ShouldContain("EUniformRequirements.ViewportDimensions |");
        transformTool.ShouldContain("EUniformRequirements.ClipSpacePolicy");
    }

    [Test]
    public void OpenGlBindBuffers_AllowsAttributeLessVertexIdPrograms()
    {
        string meshRenderer = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "OpenGL",
            "Types",
            "Mesh Renderer",
            "GLMeshRenderer.Buffers.cs"));
        string renderProgram = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "OpenGL",
            "Types",
            "Meshes",
            "GLRenderProgram.cs"));
        string layoutResolver = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "OpenGL",
            "Types",
            "Meshes",
            "GLShaderAttributeLayoutResolver.cs"));

        meshRenderer.ShouldContain("program.UsesVertexIdOnlyVertexInput()");
        meshRenderer.ShouldContain("vertexAttributesBound == 0 && arrayBuffersSeen > 0 && !usesVertexIdOnlyVertexInput");
        meshRenderer.ShouldContain("else if (usesVertexIdOnlyVertexInput)");
        meshRenderer.ShouldContain("program uses gl_VertexID without vertex attributes");
        renderProgram.ShouldContain("internal bool UsesVertexIdOnlyVertexInput()");
        layoutResolver.ShouldContain("UsesVertexIdWithoutVertexInputs");
        layoutResolver.ShouldContain("\\bgl_VertexID\\b");
        layoutResolver.ShouldContain("VertexInputDeclarationRegex");
    }

    [Test]
    public void DeferredLightCombine_UsesFramebufferUvForGBufferComposite()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "DeferredLightCombine.fs"));

        source.ShouldContain("uniform float ScreenWidth;");
        source.ShouldContain("uniform float ScreenHeight;");
        source.ShouldContain("uniform vec2 ScreenOrigin;");
        source.ShouldContain("XRENGINE_FramebufferUV(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight))");
        source.ShouldContain("XRENGINE_FramebufferPixelLocal(gl_FragCoord.xy, ScreenOrigin)");
        source.ShouldNotContain("XRENGINE_ScreenUV(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight))");
        source.ShouldNotContain("XRENGINE_ScreenPixelLocal(gl_FragCoord.xy, vec2(0.0), vec2(textureSize(DepthView)))");
        source.ShouldNotContain("vec2 uv = FragPos.xy;");
        source.ShouldNotContain("uv = uv * 0.5f + 0.5f;");
    }

    [TestCase("Scene3D/DeferredLightingDir.fs")]
    [TestCase("Scene3D/DeferredLightingPoint.fs")]
    [TestCase("Scene3D/DeferredLightingSpot.fs")]
    public void DeferredLightAccumulationPasses_UseFramebufferCoordinatesForGBufferReads(string shaderRelativePath)
    {
        string source = LoadShaderSource(shaderRelativePath);

        source.ShouldContain("XRENGINE_FramebufferCoordLocal(gl_FragCoord.xy, ScreenOrigin)");
        source.ShouldNotContain("XRENGINE_ScreenCoordLocal(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight))");
    }

    [Test]
    public void DeferredLightingEnhanced_UsesFramebufferUvForGBufferReads()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "DeferredLightingDir_Enhanced.fs"));

        source.ShouldContain("XRENGINE_FramebufferUV(gl_FragCoord.xy, vec2(ScreenWidth, ScreenHeight))");
        source.ShouldNotContain("XRENGINE_ScreenUV(gl_FragCoord.xy, vec2(ScreenWidth, ScreenHeight))");
    }

    [Test]
    public void ScreenSpaceUtils_ConvertsFullscreenClipCoordinatesWithRuntimeYDirection()
    {
        string source = LoadShaderSource(Path.Combine("Snippets", "ScreenSpaceUtils.glsl"));
        string depthUtils = LoadShaderSource(Path.Combine("Snippets", "DepthUtils.glsl"));

        source.ShouldContain("vec2 XRENGINE_ClipXYToScreenUV(vec2 clipXY)");
        source.ShouldContain("vec2 uv = clipXY * 0.5 + 0.5;");
        source.ShouldContain("if (ClipSpaceYDirection == 1)");
        source.ShouldContain("uv.y = 1.0 - uv.y;");
        depthUtils.ShouldContain("vec2 XRENGINE_ClipXYToScreenUV(vec2 clipXY)");
        depthUtils.ShouldContain("vec2 XRENGINE_FramebufferUV(vec2 fragCoord, vec2 screenOrigin, vec2 screenSize)");
    }

    [TestCase("Scene3D/PostProcess.fs")]
    [TestCase("Scene3D/PostProcessStereo.fs")]
    [TestCase("Scene3D/FinalPostProcess.fs")]
    [TestCase("Scene3D/FinalPostProcessStereo.fs")]
    public void FullscreenCompositePasses_UseClipPolicyForTriangleUvs(string shaderRelativePath)
    {
        string source = LoadShaderSource(shaderRelativePath);

        source.ShouldContain("#pragma snippet \"ScreenSpaceUtils\"");
        source.ShouldContain("vec2 clipXY = FragPos.xy;");
        source.ShouldContain("XRENGINE_ClipXYToScreenUV(clipXY)");
        source.ShouldNotContain("uv = uv * 0.5f + 0.5f;");
        source.ShouldNotContain("uv = uv * 0.5 + 0.5;");
    }

    [TestCase("Scene3D/Atmosphere/AtmosphereHalfDepthDownsample.fs", false)]
    [TestCase("Scene3D/Atmosphere/AtmosphereAerialPerspective.fs", true)]
    [TestCase("Scene3D/Atmosphere/AtmosphereReproject.fs", true)]
    [TestCase("Scene3D/Atmosphere/AtmosphereUpscale.fs", true)]
    [TestCase("Scene3D/VolumetricFog/VolumetricFogHalfDepthDownsample.fs", false)]
    [TestCase("Scene3D/VolumetricFog/VolumetricFogScatter.fs", true)]
    [TestCase("Scene3D/VolumetricFog/VolumetricFogReproject.fs", true)]
    [TestCase("Scene3D/VolumetricFog/VolumetricFogUpscale.fs", true)]
    public void AtmosphereAndVolumetricFullscreenPasses_UseClipPolicyForUvAndDepth(string shaderRelativePath, bool reconstructsDepth)
    {
        string source = LoadShaderSource(shaderRelativePath);

        source.ShouldContain("#pragma snippet \"ScreenSpaceUtils\"");
        source.ShouldContain("XRENGINE_ClipXYToScreenUV(ndc)");
        source.ShouldNotContain("vec3(uv, rawDepth) * 2.0f - 1.0f");

        if (reconstructsDepth)
            source.ShouldContain("uniform int ClipDepthRange;");
    }

    [Test]
    public void FullscreenCompositeFbos_RequestClipSpacePolicyUniforms()
    {
        string pipeline = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "Pipelines",
            "Types",
            "DefaultRenderPipeline.FBOs.cs"));
        string pipeline2 = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "Pipelines",
            "Types",
            "DefaultRenderPipeline2.FBOs.cs"));
        string atmosphereSky = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Scene",
            "Components",
            "Environment",
            "AtmosphericScatteringComponent.cs"));

        foreach (string source in new[] { pipeline, pipeline2 })
        {
            source.ShouldContain("RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.Lights | EUniformRequirements.RenderTime | EUniformRequirements.ClipSpacePolicy");
            source.ShouldContain("RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ClipSpacePolicy");
            source.ShouldContain("RequiredEngineUniforms = EUniformRequirements.RenderTime | EUniformRequirements.ClipSpacePolicy");
            source.ShouldContain("RequiredEngineUniforms = EUniformRequirements.Lights | EUniformRequirements.RenderTime | EUniformRequirements.ClipSpacePolicy");
        }

        atmosphereSky.ShouldContain("RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.RenderTime | EUniformRequirements.ClipSpacePolicy");
    }

    [Test]
    public void ClipSpacePolicy_SynchronizesBackendStateAndDepthUtilities()
    {
        string uniforms = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Resources",
            "Shaders",
            "EEngineUniform.cs"));
        string uniformRequirements = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Materials",
            "Options",
            "EUniformRequirements.cs"));
        string openGlClipSpace = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "OpenGL",
            "OpenGLRenderer.ClipSpace.cs"));
        string openGlFramebuffer = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "OpenGL",
            "OpenGLRenderer.Framebuffer.cs"));
        string screenSpaceUiCommand = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "Pipelines",
            "Commands",
            "VPRC_RenderScreenSpaceUI.cs"));
        string xrViewport = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "XRViewport.cs"));
        string ultralightGlDriver = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "UI",
            "Ultralight",
            "OpenGLGPUDriver.cs"));
        string vulkanState = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "Commands",
            "VulkanRenderer.StateTracking.cs"));
        string vulkanImGui = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "UI",
            "VulkanRenderer.ImGui.cs"));
        string vulkanExtensions = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "Bootstrap",
            "VulkanExtensions.cs"));
        string vulkanDepthClipControl = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "Types",
            "VulkanDepthClipControlExt.cs"));
        string vulkanPipeline = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "BackendObjects",
            "MeshRendering",
            "VkMeshRenderer.Pipeline.cs"));
        string vulkanShaderTools = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "Shaders",
            "VulkanShaderSourceFixups.cs"));
        string depthUtils = LoadShaderSource(Path.Combine("Snippets", "DepthUtils.glsl"));
        string plan = ReadWorkspaceFile(Path.Combine(
            "docs",
            "architecture",
            "rendering",
            "vulkan-default-pipeline-issue-plan.md"));

        uniforms.ShouldContain("ClipSpaceYDirection");
        uniforms.ShouldContain("ClipDepthRange");
        uniformRequirements.ShouldContain("ClipSpacePolicy = 64");
        uniformRequirements.ShouldContain("[nameof(EEngineUniform.ClipSpaceYDirection)] = EUniformRequirements.ClipSpacePolicy");
        openGlClipSpace.ShouldContain("api.ClipControl(ToGLClipOrigin(yDirection), ToGLClipDepthRange(depthRange));");
        vulkanState.ShouldContain("ClipDepthRange=NegativeOneToOne was requested");
        vulkanState.ShouldContain("remapping vertex shader gl_Position.z");
        vulkanState.ShouldContain("Height = -(float)extent.Height");
        vulkanState.ShouldContain("ClipSpaceYDirection == ERenderClipSpaceYDirection.YDown");
        vulkanExtensions.ShouldContain("VulkanDepthClipControlExt.ExtensionName");
        vulkanDepthClipControl.ShouldContain("PhysicalDeviceDepthClipControlFeaturesEXTNative");
        vulkanDepthClipControl.ShouldContain("PipelineViewportDepthClipControlCreateInfoEXTNative");
        vulkanPipeline.ShouldContain("viewportState.PNext = &depthClipControlInfo;");
        vulkanShaderTools.ShouldContain("XRENGINE_ApplyVulkanClipDepthRemap");
        vulkanShaderTools.ShouldContain("gl_Position.z = gl_Position.z * 0.5 + gl_Position.w * 0.5;");
        depthUtils.ShouldContain("uniform int ClipDepthRange;");
        depthUtils.ShouldContain("uniform int ClipSpaceYDirection;");
        depthUtils.ShouldContain("float XRENGINE_DepthToClipZ(float depth)");
        depthUtils.ShouldContain("vec2 XRENGINE_ScreenCoordLocal");
        depthUtils.ShouldContain("ClipDepthRange == 1 ? depth * 2.0 - 1.0 : depth");
        openGlClipSpace.ShouldContain("PushUiClipSpacePolicy");
        openGlClipSpace.ShouldContain("ReapplyActiveOpenGLRenderAreaState");
        openGlFramebuffer.ShouldContain("Api.Viewport(region.X, region.Y, (uint)region.Width, (uint)region.Height);");
        openGlFramebuffer.ShouldContain("Api.Scissor(region.X, region.Y, (uint)region.Width, (uint)region.Height);");
        openGlFramebuffer.ShouldNotContain("ConvertEngineRectangleToOpenGLClipOrigin");
        screenSpaceUiCommand.ShouldContain("PushUiClipSpacePolicy");
        xrViewport.ShouldContain("PushUiClipSpacePolicy");
        ultralightGlDriver.ShouldContain("viewportHeight - gpuState.ScissorRect.Bottom");
        vulkanImGui.ShouldContain("Viewport imguiViewport = CreateImGuiViewport(fbWidth, fbHeight);");
        vulkanImGui.ShouldContain("Api.CmdSetViewport(commandBuffer, 0, 1, &imguiViewport);");
        vulkanImGui.ShouldContain("private static Viewport CreateImGuiViewport(uint framebufferWidth, uint framebufferHeight)");
        vulkanImGui.ShouldContain("Height = framebufferHeight");
        vulkanShaderTools.ShouldContain("ApplyVulkanClipDepthRemapBeforeGeometryEmit");
        vulkanShaderTools.ShouldContain("ApplyVulkanClipDepthRemapToMeshPositionAssignments");
        plan.ShouldContain("VK_EXT_depth_clip_control");
        plan.ShouldContain("shader-position remap");
    }

    [Test]
    public void ClipDepthReconstructionPasses_UseRuntimeDepthRange()
    {
        string openGlUniformBinding = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "OpenGL",
            "Types",
            "Meshes",
            "GLRenderProgram.UniformBinding.cs"));
        string postProcess = LoadShaderSource(Path.Combine("Scene3D", "PostProcess.fs"));
        string volumetricFog = LoadShaderSource(Path.Combine("Scene3D", "VolumetricFog", "VolumetricFogScatter.fs"));

        openGlUniformBinding.ShouldContain("_engineUniformClipSpaceYDirection == clipSpaceYDirection");
        openGlUniformBinding.ShouldContain("_engineUniformClipDepthRange == clipDepthRange");
        postProcess.ShouldContain("uniform int ClipDepthRange;");
        postProcess.ShouldContain("XRENGINE_PostProcessDepthToClipZ(depth)");
        postProcess.ShouldNotContain("vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f)");
        volumetricFog.ShouldContain("uniform int ClipDepthRange;");
        volumetricFog.ShouldContain("XRENGINE_VolumetricFogDepthToClipZ(depth)");
        volumetricFog.ShouldNotContain("vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f)");
    }

    [Test]
    public void SkyboxVertex_PrecomputesWorldRayAndRotation()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "Skybox.vs"));

        source.ShouldContain("layout(location = 1) out vec3 FragWorldDir;");
        source.ShouldContain("uniform mat4 InverseViewMatrix;");
        source.ShouldContain("uniform mat4 InverseProjMatrix;");
        source.ShouldContain("uniform int DepthMode;");
        source.ShouldContain("uniform int ClipDepthRange;");
        source.ShouldContain("uniform float SkyboxRotation = 0.0;");
        source.ShouldContain("vec3 GetWorldRay(vec2 clipXY)");
        source.ShouldContain("vec3 RotateSkyDirection(vec3 dir)");
        source.ShouldContain("float GetFarClipZ()");
        source.ShouldContain("return ClipDepthRange == 1 ? -1.0 : 0.0;");
        source.ShouldContain("vec2 clipXY = Position.xy;");
        source.ShouldContain("FragWorldDir = RotateSkyDirection(GetWorldRay(clipXY));");
        source.ShouldContain("gl_Position = vec4(clipXY, GetFarClipZ(), 1.0);");
        source.ShouldNotContain("GetRayClipXY");
        source.ShouldNotContain("gl_VertexIndex");
    }

    [TestCase("Scene3D/SkyboxEquirect.fs")]
    [TestCase("Scene3D/SkyboxOctahedral.fs")]
    [TestCase("Scene3D/SkyboxCubemap.fs")]
    [TestCase("Scene3D/SkyboxCubemapArray.fs")]
    [TestCase("Scene3D/SkyboxGradient.fs")]
    [TestCase("Scene3D/SkyboxDynamic.fs")]
    public void SkyboxFragments_ConsumeInterpolatedWorldDirection(string shaderRelativePath)
    {
        string source = LoadShaderSource(shaderRelativePath);
        bool normalizesWorldDirection =
            source.Contains("vec3 dir = normalize(FragWorldDir);", StringComparison.Ordinal)
            || source.Contains("vec3 dir = SafeNormalize3(FragWorldDir);", StringComparison.Ordinal);

        source.ShouldContain("layout (location = 1) in vec3 FragWorldDir;");
        normalizesWorldDirection.ShouldBeTrue();
        source.ShouldNotContain("GetWorldDirection(");
        source.ShouldNotContain("uniform mat4 InverseProjMatrix;");
        source.ShouldNotContain("uniform mat4 InverseViewMatrix;");
    }

    [Test]
    public void SkyboxDynamic_AvoidsZeroVectorNormalization()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "SkyboxDynamic.fs"));

        source.ShouldContain("vec2 SafeNormalize2(vec2 v)");
        source.ShouldContain("vec3 SafeNormalize3(vec3 v)");
        source.ShouldContain("vec3 dir = SafeNormalize3(FragWorldDir);");
        source.ShouldNotContain("vec2 st = normalize(dir.xz)");
        source.ShouldNotContain("vec2 cloudUv = normalize(max(abs(dir.y), 0.06) * dir.xz)");
        source.ShouldNotContain("SafeNormalize2(dir.xz)");
        source.ShouldNotContain("SafeNormalize2(max(abs(dir.y), 0.06) * dir.xz)");
    }

    [Test]
    public void SkyboxDynamic_UsesSeamlessSphericalSampling()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "SkyboxDynamic.fs"));

        // Stars, galaxy detail, and clouds must sample 3D noise directly on the direction vector
        // to avoid the diagonal seam + pole stretch that octahedral UV mapping produces.
        source.ShouldContain("vec3 starP = dir * starDensity;");
        source.ShouldContain("float starHash = Hash3(starCell);");
        source.ShouldContain("vec3 galaxyP = dir * 5.2 + galacticUp * 3.1 + vec3(3.1, 7.4, 1.9);");
        source.ShouldContain("float mwDetail = smoothstep(0.35, 0.95, Fbm3(galaxyP));");
        source.ShouldContain("vec3 cloudP = dir * SkyCloudScale");
        source.ShouldContain("float cloudBase = Fbm3_6(warped);");
        source.ShouldContain("float cloudMask = smoothstep(-0.08, 0.12, dir.y);");

        // The old octahedral-based UVs must not be used for stars, galaxy detail, or clouds anymore.
        source.ShouldNotContain("vec2 starUv = DirectionToOctahedralPlane(dir)");
        source.ShouldNotContain("vec2 galaxyUv = DirectionToOctahedralPlane(dir)");
        source.ShouldNotContain("vec2 cloudUv = DirectionToOctahedralPlane(dir)");
    }

    [Test]
    public void SkyboxComponent_FallbackSources_MatchVertexDirectionContract()
    {
        string source = ReadWorkspaceFile(Path.Combine("XREngine.Runtime.Rendering", "Scene", "Components", "Misc", "SkyboxComponent.cs"));
        bool normalizesWorldDirection =
            source.Contains("vec3 dir = normalize(FragWorldDir);", StringComparison.Ordinal)
            || source.Contains("vec3 dir = SafeNormalize3(FragWorldDir);", StringComparison.Ordinal);

        source.ShouldContain("Skybox shaders reconstruct and rotate view rays in the vertex stage");
        source.ShouldContain("RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ClipSpacePolicy");
        source.ShouldContain("layout(location = 1) out vec3 FragWorldDir;");
        source.ShouldContain("uniform int DepthMode;");
        source.ShouldContain("uniform int ClipDepthRange;");
        source.ShouldContain("float GetFarClipZ()");
        source.ShouldContain("return ClipDepthRange == 1 ? -1.0 : 0.0;");
        source.ShouldContain("FragWorldDir = RotateSkyDirection(GetWorldRay(clipXY));");
        source.ShouldContain("gl_Position = vec4(clipXY, GetFarClipZ(), 1.0);");
        source.ShouldNotContain("GetRayClipXY");
        source.ShouldContain("layout (location = 1) in vec3 FragWorldDir;");
        normalizesWorldDirection.ShouldBeTrue();
    }

    [Test]
    public void BackgroundSkyMaterials_DisableBlendStateInheritance()
    {
        string skyboxSource = ReadWorkspaceFile(Path.Combine("XREngine.Runtime.Rendering", "Scene", "Components", "Misc", "SkyboxComponent.cs"));
        string atmosphereSource = ReadWorkspaceFile(Path.Combine("XREngine.Runtime.Rendering", "Scene", "Components", "Environment", "AtmosphericScatteringComponent.cs"));

        skyboxSource.ShouldContain("BlendModeAllDrawBuffers = BlendMode.Disabled()");
        atmosphereSource.ShouldContain("BlendModeAllDrawBuffers = BlendMode.Disabled()");
    }

    [Test]
    public void SkyboxComponent_FallbackDynamicSource_AvoidsZeroVectorNormalization()
    {
        string source = ReadWorkspaceFile(Path.Combine("XREngine.Runtime.Rendering", "Scene", "Components", "Misc", "SkyboxComponent.cs"));

        source.ShouldContain("vec2 SafeNormalize2(vec2 v)");
        source.ShouldContain("vec3 SafeNormalize3(vec3 v)");
        source.ShouldContain("vec3 dir = SafeNormalize3(FragWorldDir);");
        source.ShouldNotContain("vec2 st = normalize(dir.xz)");
        source.ShouldNotContain("vec2 cloudUv = normalize(max(abs(dir.y), 0.06) * dir.xz)");
        source.ShouldNotContain("SafeNormalize2(dir.xz)");
        source.ShouldNotContain("SafeNormalize2(max(abs(dir.y), 0.06) * dir.xz)");
    }

    [Test]
    public void SkyboxComponent_FallbackDynamicSource_UsesSeamlessSphericalSampling()
    {
        string source = ReadWorkspaceFile(Path.Combine("XREngine.Runtime.Rendering", "Scene", "Components", "Misc", "SkyboxComponent.cs"));

        source.ShouldContain("vec3 starP = dir * starDensity;");
        source.ShouldContain("float starHash = Hash3(starCell);");
        source.ShouldContain("vec3 galaxyP = dir * 5.2 + galacticUp * 3.1 + vec3(3.1, 7.4, 1.9);");
        source.ShouldContain("float mwDetail = smoothstep(0.35, 0.95, Fbm3(galaxyP));");
        source.ShouldContain("vec3 cloudP = dir * SkyCloudScale");
        source.ShouldContain("float cloudBase = Fbm3_6(warped);");
        source.ShouldContain("float cloudMask = smoothstep(-0.08, 0.12, dir.y);");

        source.ShouldNotContain("vec2 starUv = DirectionToOctahedralPlane(dir)");
        source.ShouldNotContain("vec2 galaxyUv = DirectionToOctahedralPlane(dir)");
        source.ShouldNotContain("vec2 cloudUv = DirectionToOctahedralPlane(dir)");
    }

    [Test]
    public void DefaultRenderPipeline2_PostProcessFbo_AttachesUniformCallback()
    {
        string source = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "Pipelines",
            "Types",
            "DefaultRenderPipeline2.FBOs.cs"));

        source.ShouldContain("PostProcessFBO.SettingUniforms += ApplyPostProcessProgramBindings;");
    }

    [Test]
    public void DefaultRenderPipeline_PostProcessDiagnostics_AreEnvGated()
    {
        string flags = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Runtime",
            "RenderDiagnosticsFlags.cs"));
        string postProcess = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "Pipelines",
            "Types",
            "DefaultRenderPipeline.PostProcessing.cs"));
        string descriptors = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "Objects",
            "Types",
            "MeshRenderer",
            "VkMeshRenderer.Descriptors.cs"));

        flags.ShouldContain("public static volatile bool DiagPostProcess;");
        flags.ShouldContain("XRE_DIAG_POSTPROCESS");
        flags.ShouldContain("SetDiagPostProcess");

        postProcess.ShouldContain("if (RenderDiagnosticsFlags.DiagPostProcess)");
        postProcess.ShouldContain("[PostProcessDiag] OutputHDR=");
        postProcess.ShouldContain("TexLabel(HDRSceneTextureName)");

        descriptors.ShouldContain("if (!RenderDiagnosticsFlags.DiagPostProcess || !IsPostProcessSampler(binding.Name))");
        descriptors.ShouldContain("[PostProcessDiag] Descriptor name=");
        descriptors.ShouldContain("HDRSceneTex");
        descriptors.ShouldContain("AutoExposureTex");
        descriptors.ShouldContain("VolumetricFogColor");
    }

    [Test]
    public void VulkanUniformWriters_AcceptEngineColorStructsForVectorUniforms()
    {
        string meshUniforms = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "Objects",
            "Types",
            "MeshRenderer",
            "VkMeshRenderer.Uniforms.cs"));
        string renderProgram = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "Objects",
            "Types",
            "VkRenderProgram.cs"));
        string material = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "Objects",
            "Types",
            "VkMaterial.cs"));

        meshUniforms.ShouldContain("using XREngine.Data.Colors;");
        meshUniforms.ShouldContain("case ColorF3 c:");
        meshUniforms.ShouldContain("case ColorF4 c:");

        renderProgram.ShouldContain("using XREngine.Data.Colors;");
        renderProgram.ShouldContain("value is ColorF3 c3");
        renderProgram.ShouldContain("value is ColorF4 c4");

        material.ShouldContain("using XREngine.Data.Colors;");
        material.ShouldContain("case EShaderVarType._vec3 when value is ColorF3 c3:");
        material.ShouldContain("case EShaderVarType._vec4 when value is ColorF4 c4:");
    }

    [Test]
    public void VulkanDepthStencilDescriptors_UseStencilOnlyViewForStencilSamplers()
    {
        string postProcessShader = LoadShaderSource(Path.Combine("Scene3D", "PostProcess.fs"));
        string descriptorSource = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "Objects",
            "Types",
            "IVkImageDescriptorSource.cs"));
        string imageBackedTexture = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "Objects",
            "Types",
            "Textures",
            "VkImageBackedTexture.cs"));
        string textureView = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "Objects",
            "Types",
            "Textures",
            "VkTextureView.cs"));
        string meshDescriptors = ReadWorkspaceFile(Path.Combine(
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan",
            "Objects",
            "Types",
            "MeshRenderer",
            "VkMeshRenderer.Descriptors.cs"));

        postProcessShader.ShouldContain("uniform usampler2D StencilView;");
        descriptorSource.ShouldContain("ImageView GetStencilOnlyDescriptorView()");
        imageBackedTexture.ShouldContain("ImageAspectFlags.StencilBit");
        imageBackedTexture.ShouldContain("GetStencilOnlyDescriptorView()");
        textureView.ShouldContain("private ImageView _stencilOnlyView;");
        textureView.ShouldContain("GetAspectOnlyDescriptorView(ImageAspectFlags.StencilBit, ref _stencilOnlyView)");
        meshDescriptors.ShouldContain("RequiresStencilOnlyDescriptor(binding)");
        meshDescriptors.ShouldContain("source.GetStencilOnlyDescriptorView()");
        meshDescriptors.ShouldContain("stencil-only");
    }

    private static string LoadShaderSource(string shaderRelativePath)
        => ReadWorkspaceFile(Path.Combine("Build", "CommonAssets", "Shaders", shaderRelativePath));

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}
