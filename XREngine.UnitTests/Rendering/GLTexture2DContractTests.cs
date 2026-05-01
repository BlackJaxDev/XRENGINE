using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GLTexture2DContractTests
{
    [Test]
    public void GLTexture2D_ClampsMaxMipLevelToAllocatedStorage()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");

        source.ShouldContain("int allocatedMaxLevel = _allocatedLevels > 0");
        source.ShouldContain("return Math.Max(baseLevel, Math.Min(allocatedMaxLevel, configuredMaxLevel));");
        source.ShouldContain("Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in maxLevel);");
    }

    [Test]
    public void GLTexture2D_ProgressiveUploadKeepsPartialMipRangeHiddenAcrossBinds()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");

        source.ShouldContain("private int _progressiveVisibleBaseLevel = -1;");
        source.ShouldContain("SetProgressiveVisibleMipRange(seedBase, seedMax);");
        source.ShouldContain("if (_progressiveVisibleBaseLevel >= 0)");
        source.ShouldContain("maxLevel = Math.Min(maxLevel, Math.Max(baseLevel, _progressiveVisibleMaxLevel));");
    }

    [Test]
    public void GLTexture2D_ClearsStaleUnpackStateBeforeCpuMipUploads()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");

        source.ShouldContain("private static void ResetUnpackStateForTextureUpload(GL gl)");
        source.ShouldContain("gl.PixelStore(UnpackSkipRows, 0);");
        source.ShouldContain("gl.PixelStore(UnpackSkipPixels, 0);");
        source.ShouldContain("gl.PixelStore(UnpackSkipImages, 0);");
        source.ShouldContain("gl.PixelStore(UnpackImageHeight, 0);");
        source.ShouldContain("gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);");
        source.ShouldContain("gl.BindBuffer(GLEnum.PixelUnpackBuffer, 0);");
    }

    [Test]
    public void GLTexture2D_SparseStreamingAlsoClearsStaleUnpackStateBeforeCpuMipUploads()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.SparseStreaming.cs");
        string asyncSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.SparseStreaming.Async.cs");

        source.ShouldContain("ResetUnpackStateForTextureUpload();");
        asyncSource.ShouldContain("ResetUnpackStateForTextureUpload();");
        asyncSource.ShouldContain("ResetUnpackStateForTextureUpload(gl);");
    }

    [Test]
    public void GLTexture2D_EnsureStorageAllocatedFloorsLevelCountByMipmapChain()
    {
        // Regression: the progressive streaming coroutine pins SmallestAllowedMipmapLevel=lockMipLevel
        // before the first PushMipLevel triggers immutable storage allocation. Without a mipmap-count
        // floor, storage is sized for only `lockMipLevel + 1` levels while the resident chain has
        // many more, causing upper mip uploads to fall outside allocated storage and leaving
        // sampled content undefined.
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");

        source.ShouldContain("int mipmapCount = Math.Max(0, Mipmaps?.Length ?? 0);");
        source.ShouldContain("if (mipmapCount > requestedLevels)");
        source.ShouldContain("requestedLevels = mipmapCount;");
        source.ShouldContain("Storage level count raised by mipmap-chain floor");
    }

    [Test]
    public void GLTexture2D_LogsEveryImmutableStorageAllocation()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");

        source.ShouldContain("[GLTexture2D] Storage allocated for");
        source.ShouldContain("[GLTexture2D] DataResized scheduling immutable recreate for");
    }

    [Test]
    public void GLTexture2D_OutOfRangeMipUploadLogsDiagnosticContext()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");

        source.ShouldContain("Skipping mip upload outside allocated storage");
        source.ShouldContain("mipmapDims=");
        source.ShouldContain("allocatedDims=");
        source.ShouldContain("StreamingLockMipLevel=");
    }

    [Test]
    public void GLTexture2D_ValidatesSubImageUploadRectBeforeCallingTexSubImage2D()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");

        source.ShouldContain("TryPrepareTexSubImageUpload");
        source.ShouldContain("TryValidateTexSubImageUpload");
        source.ShouldContain("TryGetAllocatedMipDimensions");
        source.ShouldContain("uploadRect=");
        source.ShouldContain("allocatedMipDims=");
        source.ShouldContain("Recreating immutable storage");
    }

    [Test]
    public void GLTexture2D_CancelsStaleProgressiveUploadsAfterStorageGenerationChanges()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");

        source.ShouldContain("private int _storageGeneration;");
        source.ShouldContain("CurrentStorageGeneration");
        source.ShouldContain("AdvanceStorageGeneration();");
        source.ShouldContain("scheduledStorageGeneration");
        source.ShouldContain("Canceling stale progressive mip upload");
    }

    [Test]
    public void GLTexture2D_SparseStorageTracksLogicalAllocationMetadata()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");

        source.ShouldContain("_allocatedWidth = request.LogicalWidth;");
        source.ShouldContain("_allocatedHeight = request.LogicalHeight;");
        source.ShouldContain("_allocatedInternalFormat = request.SizedInternalFormat;");
        source.ShouldContain("newWidth = Data.SparseTextureStreamingLogicalWidth;");
        source.ShouldContain("requiredLevels = Data.SparseTextureStreamingLogicalMipCount > 0");
    }

    [Test]
    public void GLTexture2D_SparseResidentUploadsUseLogicalMipLevels()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");

        source.ShouldContain("int actualMipIndex = Data.SparseTextureStreamingEnabled");
        source.ShouldContain("Data.SparseTextureStreamingResidentBaseMipLevel == int.MaxValue");
        source.ShouldContain("ClearInvalidation();");
        source.ShouldContain("int glLevel = mipLevel;");
        source.ShouldContain("int glLevel = mipIndex;");
        source.ShouldNotContain("mipLevel - (Data.SparseTextureStreamingEnabled");
        source.ShouldNotContain("mipIndex - (Data.SparseTextureStreamingEnabled");
    }

    [Test]
    public void GLTexture2D_SparseStorageAllocationUsesLogicalDimensionsAndLegalLevelCount()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");

        source.ShouldContain("bool sparseLogicalAllocation = Data.SparseTextureStreamingEnabled");
        source.ShouldContain("width = Math.Max(1u, Data.SparseTextureStreamingLogicalWidth);");
        source.ShouldContain("height = Math.Max(1u, Data.SparseTextureStreamingLogicalHeight);");
        source.ShouldContain("uint legalLevels = GetLegalMipLevelCount(width, height);");
        source.ShouldContain("Clamping storage levels");
    }

    [Test]
    public void GLTextureCube_AppliesSamplerParametersAndConfiguredMipRange()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTextureCube.cs");

        source.ShouldContain("protected override void SetParameters()");
        source.ShouldContain("Api.TextureParameter(BindingId, GLEnum.TextureLodBias, Data.LodBias);");
        source.ShouldContain("GLEnum.TextureMagFilter");
        source.ShouldContain("GLEnum.TextureMinFilter");
        source.ShouldContain("GLEnum.TextureWrapS");
        source.ShouldContain("GLEnum.TextureWrapT");
        source.ShouldContain("GLEnum.TextureWrapR");
        source.ShouldContain("int maxLevel = Math.Max(baseLevel, Data.SmallestAllowedMipmapLevel);");
        source.ShouldNotContain("int maxLevel = 1000;");
    }

    [Test]
    public void PointLightShadowCubemaps_ExposeOnlyBaseMipLevel()
    {
        string source = ReadWorkspaceFile("XRENGINE/Scene/Components/Lights/Types/PointLightComponent.cs");

        CountOccurrences(source, "SmallestAllowedMipmapLevel = 0,").ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public void GLDataBuffer_DoesNotSubDataImmutableStorageWithoutDynamicStorageBit()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Buffers/GLDataBuffer.cs");

        source.ShouldContain("!Data.StorageFlags.HasFlag(EBufferMapStorageFlags.DynamicStorage)");
        source.ShouldContain("RecreateBuffer();");
        source.ShouldContain("AllocateImmutable();");
        source.ShouldContain("PushData();");
    }

    [Test]
    public void SkinnedBoundsComputeReadsMappedGpuOutputAndUsesDynamicResetBuffer()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Compute/SkinnedMeshBoundsCalculator.cs");
        string shader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Animation/SkinnedBounds.comp");

        source.ShouldContain("StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent");
        source.ShouldContain("RangeFlags = EBufferMapRangeFlags.Read | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent");
        source.ShouldContain("StorageFlags = EBufferMapStorageFlags.DynamicStorage");
        source.ShouldContain("TryGetMappedAddress(_outputPositions, out VoidPtr mappedAddress)");
        source.ShouldContain("Vector4* ptr = (Vector4*)mappedAddress.Pointer;");

        shader.ShouldContain("atomicCompSwap(MinBoundsBits.x");
        shader.ShouldContain("atomicCompSwap(MaxBoundsBits.x");
        shader.ShouldNotContain("void atomicMinVec(inout uvec4 target");
        shader.ShouldNotContain("void atomicMaxVec(inout uvec4 target");
    }

    [Test]
    public void RenderMeshAndQuadCommandsGuardMissingPipelineState()
    {
        string meshPass = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/MeshRendering/Traditional/VPRC_RenderMeshesPassTraditional.cs");
        string meshletPass = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/MeshRendering/Meshlet/VPRC_RenderMeshesPassMeshlet.cs");
        string quadPass = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/VPRC_RenderQuadToFBO.cs");

        meshPass.ShouldContain("Engine.Rendering.State.CurrentRenderingPipeline");
        meshPass.ShouldContain("activeInstance.LastRenderingCamera");
        meshPass.ShouldContain("RenderMeshesPassTraditional.MissingPipeline");
        meshletPass.ShouldContain("Engine.Rendering.State.CurrentRenderingPipeline");
        meshletPass.ShouldContain("activeInstance.LastRenderingCamera");
        quadPass.ShouldContain("Engine.Rendering.State.CurrentRenderingPipeline");
        quadPass.ShouldContain("QuadBlit.MissingPipeline");
    }

    [Test]
    public void GLTexture2D_SparseAsyncPromotionRequiresExistingVisibleCommit()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");

        source.ShouldContain("bool hasPreviousCommit = previousCommittedBaseMipLevel != int.MaxValue;");
        source.ShouldContain("if (!hasPreviousCommit)");
        source.ShouldContain("if (isDemotion)");
        source.ShouldContain("SetSparseMipSamplingRange(currentVisibleBaseMipLevel, request.LogicalMipCount - 1);");
        source.ShouldContain("ClearInvalidation();");
    }

    [Test]
    public void GLTexture2D_LodBiasChangesAreFlushedToOpenGL()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture.cs");

        source.ShouldContain("LodBias = 1 << 4");
        source.ShouldContain("nameof(XRTexture2D.LodBias) => TexturePropertyUpdateMask.LodBias");
        source.ShouldContain("Data is XRTexture2D texture2D");
        source.ShouldContain("Api.TextureParameter(id, GLEnum.TextureLodBias, texture2D.LodBias);");
    }

    [Test]
    public void XRTexture2D_ApplyResidentDataLogsPromotion()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.ImportedStreaming.cs");

        source.ShouldContain("[ApplyResidentData]");
        source.ShouldContain("previousMipmapCount");
    }

    [Test]
    public void XRTexture2D_ProgressiveUploadTracesSeedAndCompletion()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.cs");

        source.ShouldContain("[UploadMipmaps] Seeding lockMip=");
        source.ShouldContain("[UploadMipmaps] Completed progressive upload for");
        source.ShouldContain("restored Largest=");
    }

    [Test]
    public void GLTexture2D_CanRouteAutoMipGenerationThroughDetailPreservingComputeShader()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs");

        source.ShouldContain("Engine.Rendering.Settings.UseDetailPreservingComputeMipmaps");
        source.ShouldContain("Renderer.GetOrCreateDetailPreservingMipmapProgram(imageFormat)");
        source.ShouldContain("Compute/Textures/DetailPreservingMipmaps.comp");
        source.ShouldContain("base.GenerateMipmaps();");
    }

    [Test]
    public void GLTexture_BindNeverSilentlyLeavesStaleUnitState()
    {
        // Regression guard for the "wrong texture on wrong mesh" bug:
        // when Bind() cannot issue glBindTexture (invalid GL name, or OnPreBind vetoed),
        // it MUST explicitly clear the current unit's target via BindTexture(target, 0)
        // and emit a loud diagnostic. Silently returning leaves the unit pointing at
        // whatever the previous draw bound, producing cross-material texture bleed.
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture.cs");

        source.ShouldContain("[GLTexture.Bind] Binding SKIPPED (id=InvalidBindingId)");
        source.ShouldContain("[GLTexture.Bind] Binding VETOED by OnPreBind");
        source.ShouldContain("Unit cleared to 0 to prevent stale-texture bleed.");

        // Both early-return paths must call Api.BindTexture(target, 0) before returning.
        // Count occurrences inside the Bind() method body to verify they are present.
        int bindMethodStart = source.IndexOf("public virtual void Bind()", StringComparison.Ordinal);
        bindMethodStart.ShouldBeGreaterThan(0, "Could not locate Bind() method.");
        int bindMethodEnd = source.IndexOf("private void VerifySettings", bindMethodStart, StringComparison.Ordinal);
        bindMethodEnd.ShouldBeGreaterThan(bindMethodStart, "Could not locate end of Bind() method.");
        string bindBody = source.Substring(bindMethodStart, bindMethodEnd - bindMethodStart);

        int unbindCount = CountOccurrences(bindBody, "Api.BindTexture(ToGLEnum(TextureTarget), 0);");
        unbindCount.ShouldBeGreaterThanOrEqualTo(2,
            $"Expected Bind() to explicitly unbind the active unit on both early-return paths; found {unbindCount}.");
    }

    [Test]
    public void GLMaterial_SetTextureUniformRespectsDiagnosticSettingsFlag()
    {
        // When LogMaterialTextureBindings is enabled, every (material, slot, unit, texture) binding
        // must be traced so cross-material texture bleed can be diagnosed post-hoc.
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLMaterial.cs");

        source.ShouldContain("Engine.Rendering.Settings.LogMaterialTextureBindings");
        source.ShouldContain("[GLMaterial.Bind] material=");
    }

    [Test]
    public void EngineRenderingSettings_ExposesLogMaterialTextureBindingsFlag()
    {
        string source = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs");

        source.ShouldContain("private bool _logMaterialTextureBindings = false;");
        source.ShouldContain("public bool LogMaterialTextureBindings");
    }

    [Test]
    public void TextureRuntimeDiagnostics_RoutesEventsToDedicatedTextureLog()
    {
        string debugSource = ReadWorkspaceFile("XREngine.Runtime.Core/Core/Diagnostics/Debug.cs");
        string diagnosticsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/TextureRuntimeDiagnostics.cs");
        string settingsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs");

        debugSource.ShouldContain("Textures");
        debugSource.ShouldContain("[ELogCategory.Textures] = null");
        diagnosticsSource.ShouldContain("ELogCategory.Textures");
        diagnosticsSource.ShouldContain("Texture.UploadValidationFailed");
        diagnosticsSource.ShouldContain("Texture.VramPressure");
        diagnosticsSource.ShouldContain("Texture.VramSummary");
        settingsSource.ShouldContain("TextureRuntimeLogMode");
        settingsSource.ShouldContain("TextureUploadFrameBudgetMilliseconds");
    }

    [Test]
    public void TextureStreamingManager_CoalescesAndBudgetsImportEraPromotion()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs");

        source.ShouldContain("MaxImportEraPromotionBytesPerFrame");
        source.ShouldContain("PromotionCooldownUntilFrameId");
        source.ShouldContain("DemotionCooldownUntilFrameId");
        source.ShouldContain("PinUntilFrameId");
        source.ShouldContain("LogTransitionCoalesced");
        source.ShouldContain("visible import-era promotion");
        source.ShouldContain("vram pressure demotion");
    }

    [Test]
    public void TextureStreamingPanel_ExposesQueueUploadPriorityAndFilters()
    {
        string source = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.TextureStreamingPanel.cs");

        source.ShouldContain("_textureStreamingFilterVisibleOnly");
        source.ShouldContain("_textureStreamingFilterValidationOnly");
        source.ShouldContain("TextureStreamingSortMode.QueueWait");
        source.ShouldContain("OldestQueueWaitMilliseconds");
        source.ShouldContain("LastUploadMilliseconds");
        source.ShouldContain("PriorityScore");
        source.ShouldContain("DumpImportedTextureStreamingSummary");
    }

    private static int CountOccurrences(string text, string substring)
    {
        if (string.IsNullOrEmpty(substring))
            return 0;

        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(substring, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += substring.Length;
        }
        return count;
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = ResolveWorkspacePath(repoRoot, relativePath);
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");

        // Include sibling partial-class files that share the stem (e.g. GLTexture2D.cs + GLTexture2D.Parameters.cs
        // + GLTexture2D.Storage.cs + GLTexture2D.Upload.cs). Contract tests assert source-level invariants
        // that may live in any partial of the class.
        string? directory = Path.GetDirectoryName(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        var sb = new System.Text.StringBuilder(File.ReadAllText(path));
        if (!string.IsNullOrEmpty(directory))
        {
            foreach (string partial in Directory.EnumerateFiles(directory, $"{stem}.*{extension}"))
            {
                if (string.Equals(partial, path, StringComparison.OrdinalIgnoreCase))
                    continue;
                sb.AppendLine();
                sb.Append(File.ReadAllText(partial));
            }
        }
        return sb.ToString();
    }

    private static string ResolveWorkspacePath(string repoRoot, string relativePath)
    {
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path))
            return path;

        const string legacyRenderingPrefix = "XRENGINE/Rendering/";
        if (relativePath.StartsWith(legacyRenderingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string migratedPath = "XREngine.Runtime.Rendering/Rendering/" + relativePath[legacyRenderingPrefix.Length..];
            path = Path.Combine(repoRoot, migratedPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return path;
        }

        const string legacyScenePrefix = "XRENGINE/Scene/";
        if (relativePath.StartsWith(legacyScenePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string migratedPath = "XREngine.Runtime.Rendering/Scene/" + relativePath[legacyScenePrefix.Length..];
            path = Path.Combine(repoRoot, migratedPath.Replace('/', Path.DirectorySeparatorChar));
        }

        return path;
    }

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
