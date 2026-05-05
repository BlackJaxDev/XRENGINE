using ImageMagick;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ImportedTextureStreamingPhaseTests
{
    [Test]
    public void DetermineDesiredResidentSize_WhenPromotionsBlocked_ReturnsPreviewResidentSize()
    {
        uint desired = ImportedTextureStreamingManager.DetermineDesiredResidentSize(
            new ImportedTextureStreamingPolicyInput(
                SourceWidth: 4096u,
                SourceHeight: 4096u,
                ResidentMaxDimension: 64u,
                PreviewReady: true,
                LastVisibleFrameId: 12L,
                MinVisibleDistance: 1.0f,
                MaxProjectedPixelSpan: 1024.0f,
                MaxScreenCoverage: 0.5f,
                UvDensityHint: 1.0f,
                SamplerName: "diffuseTexture"),
            frameId: 12L,
            allowPromotions: false,
            previewMaxDimension: 64u);

        desired.ShouldBe(64u);
    }

    [Test]
    public void DetermineDesiredResidentSize_WhenVisibleProjectedSpanIsTiny_ReturnsPreviewResidentSize()
    {
        uint desired = ImportedTextureStreamingManager.DetermineDesiredResidentSize(
            new ImportedTextureStreamingPolicyInput(
                SourceWidth: 4096u,
                SourceHeight: 4096u,
                ResidentMaxDimension: 64u,
                PreviewReady: true,
                LastVisibleFrameId: 12L,
                MinVisibleDistance: 500.0f,
                MaxProjectedPixelSpan: 0.25f,
                MaxScreenCoverage: 0.0f,
                UvDensityHint: 1.0f,
                SamplerName: "diffuseTexture"),
            frameId: 12L,
            allowPromotions: true,
            previewMaxDimension: 64u);

        desired.ShouldBe(64u);
    }

    [Test]
    public void DetermineDesiredResidentSize_WhenVisibleMetricsAreMissing_ReturnsPreviewResidentSize()
    {
        uint desired = ImportedTextureStreamingManager.DetermineDesiredResidentSize(
            new ImportedTextureStreamingPolicyInput(
                SourceWidth: 4096u,
                SourceHeight: 4096u,
                ResidentMaxDimension: 1u,
                PreviewReady: true,
                LastVisibleFrameId: 12L,
                MinVisibleDistance: 3.0f,
                MaxProjectedPixelSpan: 0.0f,
                MaxScreenCoverage: 0.0f,
                UvDensityHint: 1.0f,
                SamplerName: "diffuseTexture"),
            frameId: 12L,
            allowPromotions: true,
            previewMaxDimension: 64u);

        desired.ShouldBe(64u);
    }

    [Test]
    public void DetermineDesiredResidentSize_WhenVisibleNormalMapProjectedSpanIsTiny_ReturnsPreviewResidentSize()
    {
        uint desired = ImportedTextureStreamingManager.DetermineDesiredResidentSize(
            new ImportedTextureStreamingPolicyInput(
                SourceWidth: 4096u,
                SourceHeight: 4096u,
                ResidentMaxDimension: 1u,
                PreviewReady: true,
                LastVisibleFrameId: 12L,
                MinVisibleDistance: 500.0f,
                MaxProjectedPixelSpan: 0.25f,
                MaxScreenCoverage: 0.0f,
                UvDensityHint: 1.0f,
                SamplerName: "normalTexture"),
            frameId: 12L,
            allowPromotions: true,
            previewMaxDimension: 64u);

        desired.ShouldBe(64u);
    }

    [Test]
    public void DetermineDesiredResidentSize_WhenNotVisibleForManyFrames_ReturnsPreviewResidentSize()
    {
        uint desired = ImportedTextureStreamingManager.DetermineDesiredResidentSize(
            new ImportedTextureStreamingPolicyInput(
                SourceWidth: 4096u,
                SourceHeight: 4096u,
                ResidentMaxDimension: 64u,
                PreviewReady: true,
                LastVisibleFrameId: 12L,
                MinVisibleDistance: 500.0f,
                MaxProjectedPixelSpan: 0.0f,
                MaxScreenCoverage: 0.0f,
                UvDensityHint: 1.0f,
                SamplerName: "diffuseTexture"),
            frameId: 60L,
            allowPromotions: true,
            previewMaxDimension: 64u);

        desired.ShouldBe(64u);
    }

    [Test]
    public void FitResidentSizeToBudget_DemotesUntilResidentBytesFit()
    {
        long justUnder512Budget = XRTexture2D.EstimateResidentBytes(1024u, 1024u, 512u) - 1L;

        uint fitted = ImportedTextureStreamingManager.Instance.FitResidentSizeToBudget(
            new ImportedTextureStreamingBudgetInput(
                SourceWidth: 1024u,
                SourceHeight: 1024u,
                ResidentMaxDimension: 64u,
                PreviewMaxDimension: 64u,
                PageSelection: SparseTextureStreamingPageSelection.Full),
            desiredResidentSize: 1024u,
            availableManagedBytes: justUnder512Budget);

        fitted.ShouldBe(256u);
    }

    [Test]
    public void FitResidentSizeToBudget_WhenBudgetIsTooSmall_KeepsOnePixelResidentFloor()
    {
        uint fitted = ImportedTextureStreamingManager.Instance.FitResidentSizeToBudget(
            new ImportedTextureStreamingBudgetInput(
                SourceWidth: 1024u,
                SourceHeight: 1024u,
                ResidentMaxDimension: 64u,
                PreviewMaxDimension: 64u,
                PageSelection: SparseTextureStreamingPageSelection.Full),
            desiredResidentSize: 1024u,
            availableManagedBytes: 0L);

        fitted.ShouldBe(1u);
    }

    [Test]
    public void DetermineDesiredResidentSize_UsesProjectedPixelSpanAndRoleWeight()
    {
        uint albedoDesired = ImportedTextureStreamingManager.DetermineDesiredResidentSize(
            new ImportedTextureStreamingPolicyInput(
                SourceWidth: 4096u,
                SourceHeight: 4096u,
                ResidentMaxDimension: 512u,
                PreviewReady: true,
                LastVisibleFrameId: 24L,
                MinVisibleDistance: 3.0f,
                MaxProjectedPixelSpan: 1400.0f,
                MaxScreenCoverage: 0.35f,
                UvDensityHint: 1.0f,
                SamplerName: "diffuseTexture"),
            frameId: 24L,
            allowPromotions: true,
            previewMaxDimension: 64u);

        uint roughnessDesired = ImportedTextureStreamingManager.DetermineDesiredResidentSize(
            new ImportedTextureStreamingPolicyInput(
                SourceWidth: 4096u,
                SourceHeight: 4096u,
                ResidentMaxDimension: 512u,
                PreviewReady: true,
                LastVisibleFrameId: 24L,
                MinVisibleDistance: 3.0f,
                MaxProjectedPixelSpan: 1400.0f,
                MaxScreenCoverage: 0.35f,
                UvDensityHint: 1.0f,
                SamplerName: "roughnessTexture"),
            frameId: 24L,
            allowPromotions: true,
            previewMaxDimension: 64u);

        albedoDesired.ShouldBeGreaterThan(roughnessDesired);
        albedoDesired.ShouldBe(2048u);
        roughnessDesired.ShouldBe(1024u);
    }

    [Test]
    public void CalculatePromotionFadeBias_UsesPromotedMipDelta()
    {
        float lodBias = ImportedTextureStreamingManager.CalculatePromotionFadeBias(
            sourceWidth: 4096u,
            sourceHeight: 4096u,
            previousResidentSize: 256u,
            nextResidentSize: 1024u);

        lodBias.ShouldBe(2.0f);
    }

    [Test]
    public void CalculatePromotionFadeBias_DemotionOrNoChange_ReturnsZero()
    {
        ImportedTextureStreamingManager.CalculatePromotionFadeBias(4096u, 4096u, 1024u, 1024u).ShouldBe(0.0f);
        ImportedTextureStreamingManager.CalculatePromotionFadeBias(4096u, 4096u, 1024u, 256u).ShouldBe(0.0f);
    }

    [Test]
    public void SmoothPromotionFadeProgress_EasesMipPromotions()
    {
        ImportedTextureStreamingManager.SmoothPromotionFadeProgress(-1.0f).ShouldBe(0.0f);
        ImportedTextureStreamingManager.SmoothPromotionFadeProgress(0.0f).ShouldBe(0.0f);
        ImportedTextureStreamingManager.SmoothPromotionFadeProgress(0.5f).ShouldBe(0.5f, 0.0001f);
        ImportedTextureStreamingManager.SmoothPromotionFadeProgress(1.0f).ShouldBe(1.0f);
        ImportedTextureStreamingManager.SmoothPromotionFadeProgress(2.0f).ShouldBe(1.0f);

        ImportedTextureStreamingManager.SmoothPromotionFadeProgress(0.25f).ShouldBeLessThan(0.25f);
        ImportedTextureStreamingManager.SmoothPromotionFadeProgress(0.75f).ShouldBeGreaterThan(0.75f);
    }

    [Test]
    public void ShouldRecordImportedTextureStreamingUsage_OnlyAllowsMainNonShadowPass()
    {
        RenderableMesh.ShouldRecordImportedTextureStreamingUsage(isShadowPass: false, isMainPass: true).ShouldBeTrue();
        RenderableMesh.ShouldRecordImportedTextureStreamingUsage(isShadowPass: true, isMainPass: true).ShouldBeFalse();
        RenderableMesh.ShouldRecordImportedTextureStreamingUsage(isShadowPass: false, isMainPass: false).ShouldBeFalse();
    }

    [Test]
    public void StreamableTexturePayload_ReadsOnlyRequestedResidentSlice()
    {
        using MagickImage source = new(MagickColors.Red, 8, 4);
        byte[] payload = XRTexture2D.CreateTextureStreamingPayload("payload-test.png", source);

        XRTexture2D.TryReadResidentDataFromTextureStreamingPayload(
            payload,
            maxResidentDimension: 4u,
            includeMipChain: true,
            out TextureStreamingResidentData residentData).ShouldBeTrue();

        residentData.SourceWidth.ShouldBe(8u);
        residentData.SourceHeight.ShouldBe(4u);
        residentData.ResidentMaxDimension.ShouldBe(4u);
        residentData.Mipmaps.Length.ShouldBe(3);
        residentData.Mipmaps[0].Width.ShouldBe(4u);
        residentData.Mipmaps[0].Height.ShouldBe(2u);
        residentData.Mipmaps[^1].Width.ShouldBe(1u);
        residentData.Mipmaps[^1].Height.ShouldBe(1u);
    }

    [Test]
    public void TextureYamlAsset_ReadsResidentSliceFromCookedEnvelope()
    {
        using MagickImage source = new(MagickColors.Blue, 8, 4);
        XRTexture2D texture = new()
        {
            Name = "EnvelopeTexture",
            FilePath = Path.Combine(Path.GetTempPath(), "EnvelopeTexture.asset"),
            Mipmaps = XRTexture2D.GetMipmapsFromImage(source)
        };

        string yaml = AssetManager.Serializer.Serialize(texture);
        yaml.ShouldContain("Format: CookedBinary");

        byte[] assetBytes = Encoding.UTF8.GetBytes(yaml);
        XRTexture2D.TryReadResidentDataFromTextureAssetFileBytes(
            assetBytes,
            maxResidentDimension: 4u,
            includeMipChain: true,
            out TextureStreamingResidentData residentData).ShouldBeTrue();

        residentData.SourceWidth.ShouldBe(8u);
        residentData.SourceHeight.ShouldBe(4u);
        residentData.ResidentMaxDimension.ShouldBe(4u);
        residentData.Mipmaps.Length.ShouldBe(3);
        residentData.Mipmaps[0].Width.ShouldBe(4u);
        residentData.Mipmaps[0].Height.ShouldBe(2u);

        XRTexture2D roundTripped = AssetManager.Deserializer.Deserialize<XRTexture2D>(yaml);
        roundTripped.Width.ShouldBe(8u);
        roundTripped.Height.ShouldBe(4u);
        roundTripped.Mipmaps.Length.ShouldBe(texture.Mipmaps.Length);
    }

    [Test]
    public void TextureStreamingAssetUsable_RejectsSingleFullMipCachePayload()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"ImportedTextureStreamingPhaseTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string sourcePath = Path.Combine(tempDirectory, "single-full-mip.png");
            string assetPath = Path.Combine(tempDirectory, "single-full-mip.png.XREngine.Rendering.XRTexture2D.asset");
            using MagickImage source = new(MagickColors.Gold, 128, 64);
            XRTexture2D texture = new()
            {
                Name = "SingleFullMip",
                FilePath = assetPath,
                OriginalPath = sourcePath,
                AutoGenerateMipmaps = true,
                Resizable = false,
                Mipmaps = [new Mipmap2D(source)]
            };

            File.WriteAllText(assetPath, AssetManager.Serializer.Serialize(texture));

            byte[] assetBytes = File.ReadAllBytes(assetPath);
            XRTexture2D.TryReadResidentDataFromTextureAssetFileBytes(
                assetBytes,
                maxResidentDimension: 64u,
                includeMipChain: true,
                out TextureStreamingResidentData residentData).ShouldBeTrue();
            residentData.ResidentMaxDimension.ShouldBe(128u);
            residentData.Mipmaps.Length.ShouldBe(1);

            XRTexture2D.IsTextureStreamingAssetUsable(assetPath).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void TextureStreamingManifest_ReadsStreamableHeaderWithoutResidentMipHydration()
    {
        using MagickImage source = new(MagickColors.CadetBlue, 16, 8);
        byte[] payload = XRTexture2D.CreateTextureStreamingPayload("manifest-test.png", source);

        XRTexture2D.TryReadTextureStreamingManifestFromTextureStreamingPayload(
            payload,
            out TextureStreamingSourceManifest manifest).ShouldBeTrue();

        manifest.SourceWidth.ShouldBe(16u);
        manifest.SourceHeight.ShouldBe(8u);
        manifest.LogicalMipCount.ShouldBeGreaterThan(1);
        manifest.Mips.Length.ShouldBe(manifest.LogicalMipCount);
        manifest.PreviewMipIndex.ShouldBeInRange(0, manifest.Mips.Length - 1);
        manifest.Mips[0].LengthBytes.ShouldBeGreaterThan(0);
    }

    [Test]
    public void TextureUploadScheduler_OrdersVisibleRepairsAheadOfBackgroundUploads()
    {
        TextureUploadScheduler scheduler = new();
        XRTexture2D backgroundTexture = new() { RuntimeManagedProgressiveUploadActive = true };
        XRTexture2D visibleTexture = new() { RuntimeManagedProgressiveUploadActive = true };
        TextureUploadWorkItem background = new(
            new WeakReference<XRTexture2D>(backgroundTexture),
            TextureUploadKind.Promotion,
            TextureUploadSourceKind.CpuPointer,
            FirstMipLevel: 0,
            MipLevelCount: 1,
            EstimatedBytes: 4,
            CapturedStorageGeneration: 1,
            TextureUploadPriorityClass.Background,
            QueueTimestamp: TextureRuntimeDiagnostics.StartTiming());
        TextureUploadWorkItem visible = background with
        {
            Texture = new WeakReference<XRTexture2D>(visibleTexture),
            PriorityClass = TextureUploadPriorityClass.VisibleNow
        };

        scheduler.TryRegister(visibleTexture, visible).ShouldBeTrue();
        scheduler.HasHigherPriorityUpload(backgroundTexture, background).ShouldBeTrue();
    }

    [Test]
    public void TextureUploadScheduler_CoalescesDuplicateTextureWork()
    {
        TextureUploadScheduler scheduler = new();
        XRTexture2D texture = new();
        TextureUploadWorkItem workItem = new(
            new WeakReference<XRTexture2D>(texture),
            TextureUploadKind.Promotion,
            TextureUploadSourceKind.CpuPointer,
            FirstMipLevel: 0,
            MipLevelCount: 1,
            EstimatedBytes: 4,
            CapturedStorageGeneration: 1,
            TextureUploadPriorityClass.Background,
            QueueTimestamp: TextureRuntimeDiagnostics.StartTiming());

        scheduler.TryRegister(texture, workItem).ShouldBeTrue();
        scheduler.TryRegister(texture, workItem).ShouldBeFalse();
        scheduler.QueuedUploadCount.ShouldBe(1);

        scheduler.TryRemove(texture, workItem).ShouldBeTrue();
        scheduler.QueuedUploadCount.ShouldBe(0);
    }

    [Test]
    public void TextureStreamingRegistry_RecordsUsageAndBindingInSnapshots()
    {
        TextureStreamingRegistry registry = new();
        XRTexture2D texture = new()
        {
            Name = "RegistryTexture",
            FilePath = Path.Combine(Path.GetTempPath(), "registry-texture.png"),
            SamplerName = "normalTexture",
        };

        ImportedTextureStreamingRecord record = registry.GetOrCreateRecord(
            texture,
            texture.FilePath,
            _ => new GLTieredTextureResidencyBackend());
        lock (record.Sync)
        {
            record.SourceWidth = 1024u;
            record.SourceHeight = 512u;
            record.ResidentMaxDimension = 64u;
            record.PreviewReady = true;
        }

        XRMaterial material = new([texture]);
        ImportedTextureStreamingUsage usage = new(
            DistanceFromCamera: 10.0f,
            ProjectedPixelSpan: 900.0f,
            ScreenCoverage: 0.25f,
            UvDensityHint: 1.5f,
            PageSelection: SparseTextureStreamingPageSelection.Partial(0.1f, 0.2f, 0.6f, 0.8f));

        registry.RecordUsage(material, usage, frameId: 42L);
        registry.RecordMaterialBinding(material, frameId: 42L);

        List<ImportedTextureStreamingSnapshot> snapshots = registry.CollectSnapshots(
            (_, _, _) => new GLTieredTextureResidencyBackend());

        snapshots.Count.ShouldBe(1);
        ImportedTextureStreamingSnapshot snapshot = snapshots[0];
        snapshot.TextureName.ShouldBe("RegistryTexture");
        snapshot.FilePath.ShouldBe(texture.FilePath);
        snapshot.SourceWidth.ShouldBe(1024u);
        snapshot.SourceHeight.ShouldBe(512u);
        snapshot.LastVisibleFrameId.ShouldBe(42L);
        snapshot.LastBoundFrameId.ShouldBe(42L);
        snapshot.LastBoundMaterialTextureCount.ShouldBe(1);
        snapshot.MinVisibleDistance.ShouldBe(10.0f);
        snapshot.MaxProjectedPixelSpan.ShouldBe(900.0f);
        snapshot.MaxScreenCoverage.ShouldBe(0.25f);
        snapshot.UvDensityHint.ShouldBe(1.5f);
        snapshot.VisiblePageSelection.IsPartial.ShouldBeTrue();
    }

    [Test]
    public void TextureTransitionQueue_ReplacesAndClearsPendingTransitionState()
    {
        TextureTransitionQueue queue = new();
        ImportedTextureStreamingRecord record = new(new XRTexture2D());
        using CancellationTokenSource firstCts = new();
        using CancellationTokenSource secondCts = new();

        queue.TryBeginTransition(
            record,
            firstCts,
            pendingMaxDimension: 128u,
            SparseTextureStreamingPageSelection.Full,
            frameId: 10L,
            pressureDemotion: false,
            previousResidentSize: 64u,
            previousCommittedBytes: 128L,
            targetCommittedBytes: 512L,
            backendName: "first",
            reason: "first promotion",
            JobPriority.Normal,
            TextureUploadPriorityClass.Background,
            out CancellationTokenSource? previousCts).ShouldBeTrue();
        previousCts.ShouldBeNull();

        queue.TryBeginTransition(
            record,
            secondCts,
            pendingMaxDimension: 256u,
            SparseTextureStreamingPageSelection.Full,
            frameId: 11L,
            pressureDemotion: false,
            previousResidentSize: 128u,
            previousCommittedBytes: 512L,
            targetCommittedBytes: 2048L,
            backendName: "second",
            reason: "second promotion",
            JobPriority.High,
            TextureUploadPriorityClass.NearVisible,
            out previousCts).ShouldBeTrue();

        previousCts.ShouldBe(firstCts);
        lock (record.Sync)
        {
            record.PendingLoadCts.ShouldBe(secondCts);
            record.PendingMaxDimension.ShouldBe(256u);
            record.LastTransitionFrameId.ShouldBe(11L);
            record.PendingTransitionBackendName.ShouldBe("second");
            record.PendingTransitionPriority.ShouldBe(JobPriority.High);
            record.PendingTransitionUploadPriorityClass.ShouldBe(TextureUploadPriorityClass.NearVisible);

            TextureTransitionQueue.ClearPendingStateAfterLock(record);

            record.PendingLoadCts.ShouldBeNull();
            record.PendingMaxDimension.ShouldBe(0u);
            record.PendingTransitionBackendName.ShouldBeEmpty();
            record.PendingTransitionPriority.ShouldBe(JobPriority.Low);
            record.PendingTransitionUploadPriorityClass.ShouldBe(TextureUploadPriorityClass.Background);
        }
    }

    [Test]
    public void TextureStreamingCacheAsset_CreatesPreviewResidentMipChain()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"ImportedTextureStreamingPhaseTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string sourcePath = Path.Combine(tempDirectory, "streamable-source.png");
            string assetPath = Path.Combine(tempDirectory, "streamable-source.png.XREngine.Rendering.XRTexture2D.asset");
            using (MagickImage source = new(MagickColors.Orange, 128, 64))
                source.Write(sourcePath);

            DateTime sourceTimestampUtc = File.GetLastWriteTimeUtc(sourcePath);
            XRTexture2D.TryCreateTextureStreamingCacheAsset(
                sourcePath,
                assetPath,
                sourceTimestampUtc,
                out XRTexture2D texture).ShouldBeTrue();

            texture.OriginalPath.ShouldBe(sourcePath);
            texture.OriginalLastWriteTimeUtc.ShouldBe(sourceTimestampUtc);
            texture.Mipmaps.Length.ShouldBeGreaterThan(1);

            File.WriteAllText(assetPath, AssetManager.Serializer.Serialize(texture));

            XRTexture2D.IsTextureStreamingAssetUsable(assetPath).ShouldBeTrue();

            byte[] assetBytes = File.ReadAllBytes(assetPath);
            XRTexture2D.TryReadResidentDataFromTextureAssetFileBytes(
                assetBytes,
                maxResidentDimension: 64u,
                includeMipChain: true,
                out TextureStreamingResidentData residentData).ShouldBeTrue();

            residentData.SourceWidth.ShouldBe(128u);
            residentData.SourceHeight.ShouldBe(64u);
            residentData.ResidentMaxDimension.ShouldBe(64u);
            residentData.Mipmaps.Length.ShouldBeGreaterThan(1);
            residentData.Mipmaps[0].Width.ShouldBe(64u);
            residentData.Mipmaps[0].Height.ShouldBe(32u);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void TextureYamlAsset_InvalidCookedPayload_ReturnsFalse()
    {
        const string yaml = """
Format: CookedBinary
Payload:
  Length: 4
  Encoding: RawHex
  Bytes: DEADBEEF
""";

        byte[] assetBytes = Encoding.UTF8.GetBytes(yaml);

        XRTexture2D.TryReadResidentDataFromTextureAssetFileBytes(
            assetBytes,
            maxResidentDimension: 4u,
            includeMipChain: true,
            out _).ShouldBeFalse();
    }

    [Test]
    public void AssetTextureStreamingSource_WhenCacheAssetUnreadable_FallsBackToOriginalSource()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"ImportedTextureStreamingPhaseTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string sourcePath = Path.Combine(tempDirectory, "fallback-source.png");
            string assetPath = Path.Combine(tempDirectory, "fallback-source.png.XREngine.Rendering.XRTexture2D.asset");

            using (MagickImage source = new(MagickColors.Green, 8, 4))
                source.Write(sourcePath);

            File.WriteAllText(assetPath, """
Format: CookedBinary
Payload:
  Length: 4
  Encoding: RawHex
  Bytes: DEADBEEF
""");

            XRTexture2D.IsTextureStreamingAssetUsable(assetPath).ShouldBeFalse();

            AssetTextureStreamingSource streamingSource = new(assetPath, sourcePath);
            TextureStreamingResidentData residentData = streamingSource.LoadResidentData(maxResidentDimension: 4u, includeMipChain: true, CancellationToken.None);

            residentData.SourceWidth.ShouldBe(8u);
            residentData.SourceHeight.ShouldBe(4u);
            residentData.ResidentMaxDimension.ShouldBe(4u);
            residentData.Mipmaps.Length.ShouldBe(3);
            residentData.Mipmaps[0].Width.ShouldBe(4u);
            residentData.Mipmaps[0].Height.ShouldBe(2u);

            File.Delete(assetPath);

            TextureStreamingResidentData secondResidentData = streamingSource.LoadResidentData(maxResidentDimension: 4u, includeMipChain: false, CancellationToken.None);
            secondResidentData.SourceWidth.ShouldBe(8u);
            secondResidentData.SourceHeight.ShouldBe(4u);
            secondResidentData.Mipmaps.Length.ShouldBe(1);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void SparseTextureSupport_PageAlignmentUsesVirtualPageSize()
    {
        SparseTextureStreamingSupport support = new(
            SupportsSparseTextures: true,
            SupportsSparseTexture2: false,
            VirtualPageSizeX: 128u,
            VirtualPageSizeY: 128u,
            VirtualPageSizeIndex: 0);

        support.IsPageAligned(4096u, 2048u).ShouldBeTrue();
        support.IsPageAligned(4100u, 2048u).ShouldBeFalse();
        support.IsPageAligned(4096u, 2050u).ShouldBeFalse();
    }

    [Test]
    public void SparseEstimateCommittedBytes_UsesMipTailBoundary()
    {
        GLSparseTextureResidencyBackend backend = new();

        long committedBytes = backend.EstimateCommittedBytes(
            sourceWidth: 4096u,
            sourceHeight: 4096u,
            residentMaxDimension: 16u,
            format: XREngine.Data.Rendering.ESizedInternalFormat.Rgba8,
            sparseNumLevels: 7,
            pageSelection: SparseTextureStreamingPageSelection.Full);

        long expectedBytes = XRTexture2D.EstimateMipRangeBytes(
            sourceWidth: 4096u,
            sourceHeight: 4096u,
            baseMipLevel: 7,
            logicalMipCount: XRTexture2D.GetLogicalMipCount(4096u, 4096u),
            format: XREngine.Data.Rendering.ESizedInternalFormat.Rgba8);

        committedBytes.ShouldBe(expectedBytes);
    }

    [Test]
    public void SparseEstimateCommittedBytes_UsesPartialPageCoverageWhenSelected()
    {
        SparseTextureStreamingSupport support = new(
            SupportsSparseTextures: true,
            SupportsSparseTexture2: false,
            VirtualPageSizeX: 128u,
            VirtualPageSizeY: 128u,
            VirtualPageSizeIndex: 0);

        int requestedBaseMipLevel = XRTexture2D.ResolveResidentBaseMipLevel(4096u, 4096u, 1024u);
        int logicalMipCount = XRTexture2D.GetLogicalMipCount(4096u, 4096u);

        long committedBytes = XRTexture2D.EstimateSparsePageSelectionBytes(
            sourceWidth: 4096u,
            sourceHeight: 4096u,
            requestedBaseMipLevel: requestedBaseMipLevel,
            logicalMipCount: logicalMipCount,
            numSparseLevels: 7,
            support: support,
            selection: SparseTextureStreamingPageSelection.Partial(0.0f, 0.0f, 0.25f, 0.25f),
            format: XREngine.Data.Rendering.ESizedInternalFormat.Rgba8);

        long fullCommittedBytes = XRTexture2D.EstimateSparsePageSelectionBytes(
            sourceWidth: 4096u,
            sourceHeight: 4096u,
            requestedBaseMipLevel: requestedBaseMipLevel,
            logicalMipCount: logicalMipCount,
            numSparseLevels: 7,
            support: support,
            selection: SparseTextureStreamingPageSelection.Full,
            format: XREngine.Data.Rendering.ESizedInternalFormat.Rgba8);

        committedBytes.ShouldBeLessThan(fullCommittedBytes);
    }
}
