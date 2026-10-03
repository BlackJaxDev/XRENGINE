using XREngine.Imaging;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using XREngine.Data;
using XREngine.Data.Rendering;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using CookedBinaryReader = XREngine.Core.Files.RuntimeCookedBinaryReader;
using CookedBinarySerializer = XREngine.Core.Files.RuntimeCookedBinarySerializer;
using CookedBinaryWriter = XREngine.Core.Files.RuntimeCookedBinaryWriter;
using ICookedBinarySerializable = XREngine.Core.Files.IRuntimeCookedBinarySerializable;
using RuntimeCookedBinaryTypeMarker = XREngine.Core.Files.RuntimeCookedBinaryTypeMarker;

namespace XREngine.Rendering;

public partial class XRTexture2D
{
    private const int StreamableMipSectionMagic = 0x58525453; // XRTS
    private const int StreamableMipSectionVersion = 1;
    private const int TextureStreamingGpuCacheCookTimeoutMilliseconds = 120_000;
    private const int StreamableMipDescriptorSize =
        sizeof(uint) + sizeof(uint) + sizeof(int) + sizeof(int) + sizeof(int) + sizeof(long) + sizeof(int);
    private static readonly DataSourceYamlTypeConverter TextureYamlPayloadConverter = new();

    private readonly record struct TextureMipmapReadRequest(bool LoadAll, uint MaxResidentDimension, bool IncludeMipChain)
    {
        public static TextureMipmapReadRequest Full => new(true, 0u, true);
        public static TextureMipmapReadRequest Resident(uint maxResidentDimension, bool includeMipChain)
            => new(false, maxResidentDimension, includeMipChain);
    }

    private readonly record struct StreamableMipmapDescriptor(
        uint Width,
        uint Height,
        EPixelInternalFormat InternalFormat,
        EPixelFormat PixelFormat,
        EPixelType PixelType,
        long DataOffset,
        int DataLength);

    internal static byte[] CreateTextureStreamingPayload(string sourceFilePath, RuntimeImage image)
    {
        XRTexture2D texture = CreateTextureStreamingCacheTexture(sourceFilePath, image);
        return CreateTextureStreamingPayloadFromTexture(texture);
    }

    /// <summary>
    /// Serializes the given texture directly to a raw streaming-payload (XRTS) byte array.
    /// </summary>
    /// <remarks>
    /// Caller is responsible for ensuring <paramref name="texture"/> is shaped for streaming
    /// (full mip chain, expected format), typically by routing it through
    /// <see cref="TryCreateTextureStreamingCacheAsset"/> first.
    /// </remarks>
    internal static byte[] CreateTextureStreamingPayloadFromTexture(XRTexture2D texture)
    {
        byte[] payload = new byte[GetTextureStreamingPayloadSize(texture)];
        WriteTextureStreamingPayload(texture, payload);
        return payload;
    }

    /// <summary>Exact byte size of the streaming payload for <paramref name="texture"/>.</summary>
    internal static int GetTextureStreamingPayloadSize(XRTexture2D texture)
    {
        long size = ((ICookedBinarySerializable)texture).CalculateCookedBinarySize();
        if (size > int.MaxValue)
            throw new InvalidOperationException($"Texture streaming payload exceeds maximum supported size ({size} bytes).");

        return (int)size;
    }

    /// <summary>
    /// Writes the streaming payload into caller-owned storage sized by
    /// <see cref="GetTextureStreamingPayloadSize"/>. No intermediate array is allocated.
    /// </summary>
    internal static void WriteTextureStreamingPayload(XRTexture2D texture, Span<byte> destination)
    {
        CookedBinaryWriter writer = new(destination);
        ((ICookedBinarySerializable)texture).WriteCookedBinary(writer);
    }

    /// <summary>Writes the streaming payload through a buffer writer for callers that own the output buffer.</summary>
    internal static void WriteTextureStreamingPayload(XRTexture2D texture, System.Buffers.IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int size = GetTextureStreamingPayloadSize(texture);
        if (size == 0)
            return;

        WriteTextureStreamingPayload(texture, destination.GetSpan(size)[..size]);
        destination.Advance(size);
    }

    /// <summary>
    /// Writes the given texture to disk as a pure binary streaming-payload cache file
    /// (no YAML wrapper, no hex/base64 encoding). Sets the file's LastWriteTimeUtc to
    /// <paramref name="sourceLastWriteTimeUtc"/> so the asset-manager freshness check
    /// continues to work without relying on <see cref="OriginalLastWriteTimeUtc"/>.
    /// </summary>
    /// <returns><c>true</c> if the file was written successfully.</returns>
    internal static bool WriteBinaryStreamingCacheFile(XRTexture2D texture, string cachePath, DateTime sourceLastWriteTimeUtc)
    {
        try
        {
            string? dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // The payload is built in leased storage, which is native for anything at or above the
            // large-object threshold, so writing a cache file never allocates a transient LOH array.
            string tempPath = cachePath + ".tmp";
            XREngine.Core.Files.CookedPayloadLease lease = XREngine.Core.Files.CookedPayloadBufferPool.Rent(
                GetTextureStreamingPayloadSize(texture),
                out Span<byte> payload);
            try
            {
                WriteTextureStreamingPayload(texture, payload);

                // Atomic-ish write: stage to .tmp then rename so a crash mid-write can't leave a torn cache file.
                using FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan);
                stream.Write(lease.Span);
            }
            finally
            {
                lease.Dispose();
            }

            if (File.Exists(cachePath))
                File.Delete(cachePath);
            File.Move(tempPath, cachePath);
            try { File.SetLastWriteTimeUtc(cachePath, sourceLastWriteTimeUtc); } catch { /* mtime stamp is best-effort */ }
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, $"Failed to write binary texture streaming cache file '{cachePath}'.");
            return false;
        }
    }

    internal static bool TryCreateTextureStreamingCacheAsset(
        XRTexture2D sourceTexture,
        string sourceFilePath,
        string cacheFilePath,
        DateTime sourceLastWriteTimeUtc,
        out XRTexture2D texture)
    {
        texture = new XRTexture2D();
        if (sourceTexture is null)
            return false;

        if (TryCreateTextureStreamingCacheAssetGpu(
                sourceTexture,
                sourceFilePath,
                cacheFilePath,
                sourceLastWriteTimeUtc,
                out texture))
        {
            return true;
        }

        return TryCreateTextureStreamingCacheAsset(
            sourceFilePath,
            cacheFilePath,
            sourceLastWriteTimeUtc,
            out texture);
    }

    internal static bool TryCreateTextureStreamingCacheAsset(
        string sourceFilePath,
        string cacheFilePath,
        DateTime sourceLastWriteTimeUtc,
        out XRTexture2D texture)
    {
        texture = new XRTexture2D();
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            return false;

        try
        {
            // The encoded source is read through an owner so the file bytes never become a managed array.
            using XREngine.Core.Files.CookedPayloadOwner fileBytes = RuntimeRenderingHostServices.Assets.ReadAllBytesOwned(sourceFilePath);
            using RuntimeImage sourceImage = RuntimeImageCodecs.Require().Decode(fileBytes.ReadOnlyMemory);
            texture = CreateTextureStreamingCacheTexture(sourceFilePath, GetMipmapsFromImage(sourceImage));
            texture.FilePath = cacheFilePath;
            texture.OriginalPath = sourceFilePath;
            texture.OriginalLastWriteTimeUtc = sourceLastWriteTimeUtc;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            texture = new XRTexture2D();
            return false;
        }
    }

    private static XRTexture2D CreateTextureStreamingCacheTexture(string sourceFilePath, RuntimeImage image)
        => CreateTextureStreamingCacheTexture(sourceFilePath, GetMipmapsFromImage(image));

    private static XRTexture2D CreateTextureStreamingCacheTexture(string sourceFilePath, Mipmap2D[] mipmaps)
        => new()
        {
            Name = Path.GetFileNameWithoutExtension(sourceFilePath),
            FilePath = sourceFilePath,
            MagFilter = ETexMagFilter.Linear,
            MinFilter = ETexMinFilter.LinearMipmapLinear,
            UWrap = ETexWrapMode.Repeat,
            VWrap = ETexWrapMode.Repeat,
            AlphaAsTransparency = true,
            AutoGenerateMipmaps = false,
            Resizable = false,
            SizedInternalFormat = mipmaps is { Length: > 0 }
                ? DeriveESizedInternalFormat(mipmaps[0].InternalFormat)
                : ESizedInternalFormat.Rgba8,
            Mipmaps = mipmaps
        };

    private static bool TryCreateTextureStreamingCacheAssetGpu(
        XRTexture2D sourceTexture,
        string sourceFilePath,
        string cacheFilePath,
        DateTime sourceLastWriteTimeUtc,
        out XRTexture2D texture)
    {
        texture = new XRTexture2D();
        if (!CanUseGpuTextureStreamingCacheCook(sourceTexture))
            return false;

        ManualResetEventSlim completed = new(false);
        int completionState = 0;
        bool success = false;
        Mipmap2D[]? gpuMipmaps = null;
        string failure = string.Empty;

        void Complete(bool callbackSuccess, Mipmap2D[]? callbackMipmaps, string callbackFailure)
        {
            if (Interlocked.Exchange(ref completionState, 1) != 0)
                return;

            success = callbackSuccess;
            gpuMipmaps = callbackMipmaps;
            failure = callbackFailure;
            completed.Set();
        }

        try
        {
            RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadTask(
                () =>
                {
                    AbstractRenderer? renderer = AbstractRenderer.Current;
                    if (renderer is null)
                    {
                        Complete(false, null, "No active renderer was available for GPU texture cache cooking.");
                        return;
                    }

                    renderer.TryBuildTexture2DMipChainRgba8Async(sourceTexture, Complete);
                },
                "XRTexture2D.TextureStreamingCacheGpuCook",
                RenderThreadJobKind.TextureUpload);

            if (!completed.Wait(TextureStreamingGpuCacheCookTimeoutMilliseconds))
            {
                Interlocked.Exchange(ref completionState, 1);
                RuntimeRenderingHostServices.Diagnostics.LogWarning(
                    $"Timed out waiting for GPU texture streaming cache cook for '{sourceFilePath}'. Falling back to CPU mip generation.");
                return false;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            failure = ex.Message;
            return false;
        }
        finally
        {
            completed.Dispose();
        }

        if (!success || gpuMipmaps is null || gpuMipmaps.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(failure))
            {
                RuntimeRenderingHostServices.Diagnostics.LogWarning(
                    $"GPU texture streaming cache cook failed for '{sourceFilePath}'. Falling back to CPU mip generation. {failure}");
            }

            return false;
        }

        texture = CreateTextureStreamingCacheTexture(sourceFilePath, gpuMipmaps);
        texture.FilePath = cacheFilePath;
        texture.OriginalPath = sourceFilePath;
        texture.OriginalLastWriteTimeUtc = sourceLastWriteTimeUtc;
        return true;
    }

    private static bool CanUseGpuTextureStreamingCacheCook(XRTexture2D sourceTexture)
    {
        if (RuntimeRenderingHostServices.FrameTiming.IsRenderThread ||
            !RuntimeRenderingHostServices.FrameTiming.IsRendererActive ||
            RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend != RuntimeGraphicsApiKind.OpenGL)
        {
            return false;
        }

        Mipmap2D[] mipmaps = sourceTexture.Mipmaps;
        return mipmaps is { Length: > 0 } &&
            mipmaps[0] is not null &&
            mipmaps[0].HasData() &&
            mipmaps[0].InternalFormat == EPixelInternalFormat.Rgba8 &&
            mipmaps[0].PixelFormat == EPixelFormat.Rgba &&
            mipmaps[0].PixelType == EPixelType.UnsignedByte &&
            sourceTexture.Width > 0u &&
            sourceTexture.Height > 0u &&
            !sourceTexture.MultiSample;
    }

    /// <summary>
    /// Deserializes either a raw texture streaming body or a runtime-cooked
    /// <see cref="XRTexture2D"/> envelope without routing binary bytes through YAML.
    /// </summary>
    internal static bool TryDeserializeTextureStreamingPayload(
        ReadOnlySpan<byte> payload,
        [NotNullWhen(true)] out XRTexture2D? texture)
    {
        texture = null;
        if (!LooksLikeBinaryTextureStreamingPayload(payload))
            return false;

        try
        {
            CookedBinaryReader reader = new(payload);
            if (!TryEnterTexturePayloadBody(reader))
                return false;

            XRTexture2D candidate = new();
            ((ICookedBinarySerializable)candidate).ReadCookedBinary(reader);
            if (reader.Remaining != 0)
                return false;

            texture = candidate;
            return true;
        }
        catch (Exception ex) when (ex is EndOfStreamException
            or InvalidCastException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or FormatException
            or ArgumentOutOfRangeException
            or OverflowException)
        {
            return false;
        }
    }

    internal static bool TryReadResidentDataFromTextureStreamingPayload(
        ReadOnlySpan<byte> payload,
        uint maxResidentDimension,
        bool includeMipChain,
        out TextureStreamingResidentData residentData)
    {
        residentData = default;
        // Reject non-binary payloads early (e.g. BOM-prefixed YAML asset bytes)
        // to avoid throwing/catching NotSupportedException as control flow.
        if (!LooksLikeBinaryTextureStreamingPayload(payload))
            return false;

        try
        {
            {
                    CookedBinaryReader reader = new(payload);
                    if (!TryEnterTexturePayloadBody(reader))
                        return false;

                    XRTexture2D scratch = new();
                    scratch.ReadTextureAssetBase(reader);
                    scratch.ReadTextureStreamingSettings(reader);
                    scratch.GrabPass = ReadGrabPass(reader, scratch);

                    Mipmap2D[] mipmaps = ReadMipmaps(
                        reader,
                        TextureMipmapReadRequest.Resident(maxResidentDimension, includeMipChain),
                        out uint sourceWidth,
                        out uint sourceHeight);
                    uint residentMaxDimension = mipmaps.Length > 0
                        ? Math.Max(mipmaps[0].Width, mipmaps[0].Height)
                        : 0u;

                    residentData = new TextureStreamingResidentData(
                        mipmaps,
                        sourceWidth,
                        sourceHeight,
                        residentMaxDimension);
                    return true;
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidCastException or InvalidOperationException or NotSupportedException or FormatException)
        {
            residentData = default;
            return false;
        }
    }

    internal static bool TryReadResidentDataFromTextureAssetFileBytes(
        ReadOnlySpan<byte> assetFileBytes,
        uint maxResidentDimension,
        bool includeMipChain,
        out TextureStreamingResidentData residentData)
    {
        residentData = default;
        if (assetFileBytes.IsEmpty)
            return false;

        if (LooksLikeBinaryTextureStreamingPayload(assetFileBytes))
            return TryReadResidentDataFromTextureStreamingPayload(assetFileBytes, maxResidentDimension, includeMipChain, out residentData);

        string assetYaml;
        try
        {
            assetYaml = Encoding.UTF8.GetString(assetFileBytes);
        }
        catch
        {
            return false;
        }

        if (!TryExtractTextureStreamingPayloadFromYamlAsset(assetYaml, out DataSource? payloadSource) || payloadSource is null)
            return false;

        // The decoded YAML payload is read in place from the data source; it is not copied to an array.
        using (payloadSource)
        {
            ReadOnlySpan<byte> payload = payloadSource.AsReadOnlySpan();
            try
            {
                if (TryReadResidentDataFromTextureStreamingPayload(payload, maxResidentDimension, includeMipChain, out residentData))
                    return true;
            }
            catch
            {
            }

            try
            {
                XRTexture2D? texture = CookedBinarySerializer.Deserialize(typeof(XRTexture2D), payload) as XRTexture2D;
                if (texture is null)
                    return false;

                residentData = BuildResidentDataFromLoadedTexture(texture, maxResidentDimension, includeMipChain);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static bool TryReadTextureStreamingManifestFromTextureAssetFileBytes(
        ReadOnlySpan<byte> assetFileBytes,
        out TextureStreamingSourceManifest manifest)
    {
        manifest = default;
        if (assetFileBytes.IsEmpty)
            return false;

        if (LooksLikeBinaryTextureStreamingPayload(assetFileBytes))
            return TryReadTextureStreamingManifestFromTextureStreamingPayload(assetFileBytes, out manifest);

        string assetYaml;
        try
        {
            assetYaml = Encoding.UTF8.GetString(assetFileBytes);
        }
        catch
        {
            return false;
        }

        if (!TryExtractTextureStreamingPayloadFromYamlAsset(assetYaml, out DataSource? payloadSource) || payloadSource is null)
            return false;

        using (payloadSource)
            return TryReadTextureStreamingManifestFromTextureStreamingPayload(payloadSource.AsReadOnlySpan(), out manifest);
    }

    internal static bool TryReadTextureStreamingManifestFromTextureStreamingPayload(
        ReadOnlySpan<byte> payload,
        out TextureStreamingSourceManifest manifest)
    {
        manifest = default;
        if (!LooksLikeBinaryTextureStreamingPayload(payload))
            return false;

        try
        {
            {
                    CookedBinaryReader reader = new(payload);
                    if (!TryEnterTexturePayloadBody(reader))
                        return false;

                    XRTexture2D scratch = new();
                    scratch.ReadTextureAssetBase(reader);
                    scratch.ReadTextureStreamingSettings(reader);
                    scratch.GrabPass = ReadGrabPass(reader, scratch);
                    return TryReadStreamableMipManifest(reader, scratch.SizedInternalFormat, scratch.SamplerName, out manifest);
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidCastException or InvalidOperationException or NotSupportedException or FormatException)
        {
            manifest = default;
            return false;
        }
    }

    internal static bool IsTextureStreamingAssetUsable(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
            return false;

        TextureStreamingSourceManifest manifest;
        try
        {
            FileInfo assetInfo = new(assetPath);
            if (assetInfo.Length <= 0)
                return false;

            // Only the manifest is needed, so the cache file is mapped instead of read into an array.
            using XREngine.Core.Files.CookedPayloadOwner assetBytes = XREngine.Core.Files.CookedPayloadOwner.MapFile(assetPath);
            if (!TryReadTextureStreamingManifestFromTextureAssetFileBytes(assetBytes.Span, out manifest))
                return false;
        }
        catch
        {
            return false;
        }

        uint sourceMaxDimension = Math.Max(manifest.SourceWidth, manifest.SourceHeight);
        uint expectedResidentMaxDimension = GetPreviewResidentSize(sourceMaxDimension);
        if (manifest.PreviewMipIndex < 0 || manifest.PreviewMipIndex >= manifest.Mips.Length)
            return false;

        TextureSourceMipLayout previewMip = manifest.Mips[manifest.PreviewMipIndex];
        uint previewResidentMaxDimension = Math.Max(previewMip.Width, previewMip.Height);
        if (previewResidentMaxDimension > expectedResidentMaxDimension)
            return false;

        return sourceMaxDimension <= expectedResidentMaxDimension || manifest.Mips.Length > 1;
    }

    /// <summary>
    /// Extracts the cooked payload from a YAML-wrapped texture asset. The caller owns the returned
    /// data source and reads it through <see cref="DataSource.AsReadOnlySpan"/>.
    /// </summary>
    private static bool TryExtractTextureStreamingPayloadFromYamlAsset(string assetYaml, out DataSource? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(assetYaml))
            return false;

        try
        {
            Parser parser = new(new StringReader(assetYaml));
            ConsumeDocumentStart(parser);

            if (!parser.TryConsume<MappingStart>(out _))
                return false;

            string? format = null;
            DataSource? envelopePayload = null;
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                if (!parser.TryConsume<Scalar>(out Scalar? keyScalar))
                    return false;

                string key = keyScalar.Value ?? string.Empty;
                switch (key)
                {
                    case "Format":
                        if (!parser.TryConsume<Scalar>(out Scalar? formatScalar))
                            return false;
                        format = formatScalar.Value;
                        break;
                    case "Payload":
                        envelopePayload = TextureYamlPayloadConverter.ReadYaml(parser, typeof(DataSource), static _ => null) as DataSource;
                        break;
                    default:
                        SkipYamlNode(parser);
                        break;
                }
            }

            if (!string.Equals(format, "CookedBinary", StringComparison.Ordinal)
                || envelopePayload is null
                || envelopePayload.Length == 0)
            {
                envelopePayload?.Dispose();
                return false;
            }

            payload = envelopePayload;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ConsumeDocumentStart(Parser parser)
    {
        if (parser.TryConsume<StreamStart>(out _))
        {
        }

        if (parser.TryConsume<DocumentStart>(out _))
        {
        }
    }

    private static void SkipYamlNode(Parser parser)
    {
        if (parser.TryConsume<Scalar>(out _))
            return;

        if (parser.TryConsume<AnchorAlias>(out _))
            return;

        if (parser.TryConsume<SequenceStart>(out _))
        {
            while (!parser.TryConsume<SequenceEnd>(out _))
                SkipYamlNode(parser);
            return;
        }

        if (parser.TryConsume<MappingStart>(out _))
        {
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                SkipYamlNode(parser);
                SkipYamlNode(parser);
            }
            return;
        }

        throw new YamlException("Unsupported YAML node encountered while skipping a value.");
    }

    private static bool TryEnterTexturePayloadBody(CookedBinaryReader reader)
    {
        long start = reader.Position;
        if (reader.Remaining <= 0)
            return false;

        RuntimeCookedBinaryTypeMarker marker = (RuntimeCookedBinaryTypeMarker)reader.ReadByte();
        if (marker != RuntimeCookedBinaryTypeMarker.CustomObject)
        {
            reader.Position = start;
            return true;
        }

        string typeName = reader.ReadString();
        Type? payloadType = ResolveTexturePayloadType(typeName);
        return payloadType is not null && typeof(XRTexture2D).IsAssignableFrom(payloadType);
    }

    internal static bool LooksLikeBinaryTextureStreamingPayload(ReadOnlySpan<byte> payload)
        => !payload.IsEmpty && IsBinaryTextureStreamingMarker(payload[0]);

    private static bool IsBinaryTextureStreamingMarker(byte value)
        => value <= (byte)RuntimeCookedBinaryTypeMarker.CustomObject
            && value is not (byte)'\t' and not (byte)'\n' and not (byte)'\r';

    private static Type? ResolveTexturePayloadType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        Type? resolved = AotRuntimeMetadataStore.ResolveType(typeName);
        if (resolved is not null)
            return resolved;

        if (XRRuntimeEnvironment.IsPublishedBuild)
            return null;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            resolved = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    private void ReadTextureStreamingSettings(CookedBinaryReader reader)
    {
        SamplerName = reader.ReadValue<string>();
        FrameBufferAttachment = reader.ReadValue<EFrameBufferAttachment?>() ?? FrameBufferAttachment;
        MinLOD = ReadStructOrDefault(reader, MinLOD);
        MaxLOD = ReadStructOrDefault(reader, MaxLOD);
        LargestMipmapLevel = ReadStructOrDefault(reader, LargestMipmapLevel);
        SmallestAllowedMipmapLevel = ReadStructOrDefault(reader, SmallestAllowedMipmapLevel);
        AutoGenerateMipmaps = ReadStructOrDefault(reader, AutoGenerateMipmaps);
        AlphaAsTransparency = ReadStructOrDefault(reader, AlphaAsTransparency);
        InternalCompression = ReadStructOrDefault(reader, InternalCompression);

        MagFilter = ReadStructOrDefault(reader, MagFilter);
        MinFilter = ReadStructOrDefault(reader, MinFilter);
        UWrap = ReadStructOrDefault(reader, UWrap);
        VWrap = ReadStructOrDefault(reader, VWrap);
        Rectangle = ReadStructOrDefault(reader, Rectangle);
        Resizable = ReadStructOrDefault(reader, Resizable);
        MultiSampleCount = ReadStructOrDefault(reader, MultiSampleCount);
        FixedSampleLocations = ReadStructOrDefault(reader, FixedSampleLocations);
        ExclusiveSharing = ReadStructOrDefault(reader, ExclusiveSharing);
        LodBias = ReadStructOrDefault(reader, LodBias);
        SizedInternalFormat = ReadStructOrDefault(reader, SizedInternalFormat);
    }

    private static void WriteStreamableMipmaps(CookedBinaryWriter writer, Mipmap2D[]? mipmaps)
    {
        writer.Write(StreamableMipSectionMagic);
        writer.Write(StreamableMipSectionVersion);

        int mipCount = mipmaps?.Length ?? 0;
        writer.Write(mipCount);

        int previewBaseMipIndex = ResolvePreviewBaseMipIndex(mipmaps);
        writer.Write(previewBaseMipIndex);
        if (mipCount <= 0 || mipmaps is null)
            return;

        long sectionStart = writer.Position - sizeof(int) * 4;
        long descriptorTableStart = writer.Position;
        long dataStart = descriptorTableStart + mipCount * (long)StreamableMipDescriptorSize;
        long runningDataOffset = dataStart - sectionStart;

        for (int i = 0; i < mipCount; i++)
        {
            Mipmap2D mip = mipmaps[i];
            byte[] bytes = mip.Data?.GetBytes() ?? [];
            writer.Write(mip.Width);
            writer.Write(mip.Height);
            writer.Write((int)mip.InternalFormat);
            writer.Write((int)mip.PixelFormat);
            writer.Write((int)mip.PixelType);
            writer.Write(runningDataOffset);
            writer.Write(bytes.Length);
            runningDataOffset += bytes.Length;
        }

        for (int i = 0; i < mipCount; i++)
        {
            byte[] bytes = mipmaps[i].Data?.GetBytes() ?? [];
            writer.WriteBytes(bytes);
        }
    }

    private static Mipmap2D[] ReadMipmaps(
        CookedBinaryReader reader,
        TextureMipmapReadRequest request,
        out uint sourceWidth,
        out uint sourceHeight)
    {
        long sectionStart = reader.Position;
        if (reader.Remaining >= sizeof(int) * 4)
        {
            int magic = reader.ReadInt32();
            if (magic == StreamableMipSectionMagic)
                return ReadStreamableMipmaps(reader, sectionStart, request, out sourceWidth, out sourceHeight);

            reader.Position = sectionStart;
        }

        Mipmap2D[] legacy = ReadMipmapsLegacy(reader);
        sourceWidth = legacy.Length > 0 ? legacy[0].Width : 0u;
        sourceHeight = legacy.Length > 0 ? legacy[0].Height : 0u;
        return request.LoadAll
            ? legacy
            : SelectResidentMipmaps(legacy, request.MaxResidentDimension, request.IncludeMipChain);
    }

    private static Mipmap2D[] ReadStreamableMipmaps(
        CookedBinaryReader reader,
        long sectionStart,
        TextureMipmapReadRequest request,
        out uint sourceWidth,
        out uint sourceHeight)
    {
        int version = reader.ReadInt32();
        if (version != StreamableMipSectionVersion)
            throw new InvalidOperationException($"Unsupported texture streaming mip section version '{version}'.");

        int mipCount = reader.ReadInt32();
        int previewBaseMipIndex = reader.ReadInt32();
        if (mipCount <= 0)
        {
            sourceWidth = 0u;
            sourceHeight = 0u;
            return [];
        }

        StreamableMipmapDescriptor[] descriptors = new StreamableMipmapDescriptor[mipCount];
        for (int i = 0; i < mipCount; i++)
        {
            uint width = reader.ReadUInt32();
            uint height = reader.ReadUInt32();
            EPixelInternalFormat internalFormat = (EPixelInternalFormat)reader.ReadInt32();
            EPixelFormat pixelFormat = (EPixelFormat)reader.ReadInt32();
            EPixelType pixelType = (EPixelType)reader.ReadInt32();
            long dataOffset = reader.ReadInt64();
            int dataLength = reader.ReadInt32();
            descriptors[i] = new StreamableMipmapDescriptor(width, height, internalFormat, pixelFormat, pixelType, dataOffset, dataLength);
        }

        sourceWidth = descriptors[0].Width;
        sourceHeight = descriptors[0].Height;

        int baseMipIndex;
        int endExclusive;
        if (request.LoadAll)
        {
            baseMipIndex = 0;
            endExclusive = descriptors.Length;
        }
        else
        {
            baseMipIndex = ResolveResidentBaseMipIndex(descriptors, request.MaxResidentDimension, previewBaseMipIndex);
            endExclusive = request.IncludeMipChain ? descriptors.Length : baseMipIndex + 1;
        }

        if (endExclusive <= baseMipIndex)
            return [];

        Mipmap2D[] mipmaps = new Mipmap2D[endExclusive - baseMipIndex];
        for (int index = baseMipIndex; index < endExclusive; index++)
        {
            StreamableMipmapDescriptor descriptor = descriptors[index];
            reader.Position = sectionStart + descriptor.DataOffset;
            byte[] bytes = reader.ReadBytes(descriptor.DataLength);
            mipmaps[index - baseMipIndex] = new Mipmap2D
            {
                Width = descriptor.Width,
                Height = descriptor.Height,
                InternalFormat = descriptor.InternalFormat,
                PixelFormat = descriptor.PixelFormat,
                PixelType = descriptor.PixelType,
                Data = bytes.Length == 0 ? null : new DataSource(bytes)
            };
        }

        StreamableMipmapDescriptor lastDescriptor = descriptors[^1];
        reader.Position = sectionStart + lastDescriptor.DataOffset + lastDescriptor.DataLength;
        return mipmaps;
    }

    private static bool TryReadStreamableMipManifest(
        CookedBinaryReader reader,
        ESizedInternalFormat sizedInternalFormat,
        string? samplerName,
        out TextureStreamingSourceManifest manifest)
    {
        manifest = default;
        long sectionStart = reader.Position;
        if (reader.Remaining < sizeof(int) * 4)
            return false;

        int magic = reader.ReadInt32();
        if (magic != StreamableMipSectionMagic)
            return false;

        int version = reader.ReadInt32();
        if (version != StreamableMipSectionVersion)
            return false;

        int mipCount = reader.ReadInt32();
        int previewBaseMipIndex = reader.ReadInt32();
        if (mipCount <= 0)
            return false;

        TextureSourceMipLayout[] mips = new TextureSourceMipLayout[mipCount];
        uint sourceWidth = 0u;
        uint sourceHeight = 0u;
        for (int i = 0; i < mipCount; i++)
        {
            uint width = reader.ReadUInt32();
            uint height = reader.ReadUInt32();
            _ = (EPixelInternalFormat)reader.ReadInt32();
            _ = (EPixelFormat)reader.ReadInt32();
            _ = (EPixelType)reader.ReadInt32();
            long dataOffset = reader.ReadInt64();
            int dataLength = reader.ReadInt32();
            if (i == 0)
            {
                sourceWidth = width;
                sourceHeight = height;
            }

            mips[i] = new TextureSourceMipLayout(
                i,
                sectionStart + dataOffset,
                dataLength,
                width,
                height);
        }

        manifest = new TextureStreamingSourceManifest(
            sourceWidth,
            sourceHeight,
            mipCount,
            sizedInternalFormat,
            mips,
            Math.Clamp(previewBaseMipIndex, 0, mipCount - 1),
            $"streamable-v{version}",
            ColorSpace: null,
            TextureRole: samplerName);
        return true;
    }

#if !XRE_PUBLISHED
    [RequiresUnreferencedCode("Calls XREngine.Core.Files.RuntimeCookedBinaryReader.ReadValue<T>()")]
    [RequiresDynamicCode("Calls XREngine.Core.Files.RuntimeCookedBinaryReader.ReadValue<T>()")]
#endif
    private static Mipmap2D[] ReadMipmapsLegacy(CookedBinaryReader reader)
    {
        int mipCount = ReadStructOrDefault(reader, 0);
        if (mipCount <= 0)
            return [];

        Mipmap2D[] mipmaps = new Mipmap2D[mipCount];
        for (int i = 0; i < mipCount; i++)
        {
            uint width = ReadStructOrDefault(reader, 0u);
            uint height = ReadStructOrDefault(reader, 0u);
            EPixelInternalFormat internalFormat = ReadStructOrDefault(reader, EPixelInternalFormat.Rgba8);
            EPixelFormat pixelFormat = ReadStructOrDefault(reader, EPixelFormat.Rgba);
            EPixelType pixelType = ReadStructOrDefault(reader, EPixelType.UnsignedByte);
            byte[]? bytes = reader.ReadValue<byte[]>();

            mipmaps[i] = new Mipmap2D
            {
                Width = width,
                Height = height,
                InternalFormat = internalFormat,
                PixelFormat = pixelFormat,
                PixelType = pixelType,
                Data = bytes is null ? null : new DataSource(bytes)
            };
        }

        return mipmaps;
    }

    private static long CalculateStreamableMipmapSize(Mipmap2D[]? mipmaps)
    {
        long size = sizeof(int) * 4;
        if (mipmaps is null || mipmaps.Length == 0)
            return size;

        size += mipmaps.Length * (long)StreamableMipDescriptorSize;
        for (int i = 0; i < mipmaps.Length; i++)
        {
            byte[] bytes = mipmaps[i].Data?.GetBytes() ?? [];
            size += bytes.Length;
        }

        return size;
    }

    private static int ResolvePreviewBaseMipIndex(Mipmap2D[]? mipmaps)
    {
        if (mipmaps is null || mipmaps.Length == 0)
            return 0;

        uint previewSize = GetPreviewResidentSize(Math.Max(mipmaps[0].Width, mipmaps[0].Height));
        return ResolveResidentBaseMipIndex(mipmaps, previewSize);
    }

    private static int ResolveResidentBaseMipIndex(
        StreamableMipmapDescriptor[] descriptors,
        uint maxResidentDimension,
        int previewBaseMipIndex)
    {
        if (descriptors.Length == 0)
            return 0;

        if (maxResidentDimension == 0)
            return Math.Clamp(previewBaseMipIndex, 0, descriptors.Length - 1);

        uint targetDimension = Math.Max(1u, maxResidentDimension);
        for (int index = 0; index < descriptors.Length; index++)
        {
            if (Math.Max(descriptors[index].Width, descriptors[index].Height) <= targetDimension)
                return index;
        }

        return descriptors.Length - 1;
    }

}
