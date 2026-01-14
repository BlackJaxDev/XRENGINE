using ImageMagick;
using ImageMagick.Drawing;
using MemoryPack;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using XREngine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Diagnostics;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace XREngine.Rendering
{
    [XR3rdPartyExtensions(typeof(XREngine.Data.XRTexture2DImportOptions), "png", "jpg", "jpeg", "tif", "tiff", "tga", "exr", "hdr")]
    [MemoryPackable]
    public partial class XRTexture2D : XRTexture, IFrameBufferAttachement, ICookedBinarySerializable
    {
        public override void Reload(string path)
        {
            Load3rdParty(path);
        }
        public override bool Load3rdParty(string filePath)
        {
            if (HasAssetExtension(filePath))
            {
                if (TryLoadTextureAsset(filePath, this, deepCopy: true))
                    return true;

                Debug.LogWarning($"Failed to load texture asset '{filePath}'. Falling back to filler texture.");
                return AssignFillerTexture(filePath);
            }

            if (!File.Exists(filePath))
            {
                Debug.LogWarning($"Texture file '{filePath}' does not exist. Falling back to filler texture.");
                return AssignFillerTexture(filePath);
            }

            try
            {
                var sourceImage = new MagickImage(filePath);
                Mipmaps = GetMipmapsFromImage(sourceImage);
                AutoGenerateMipmaps = false;
                Resizable = false;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to load texture from '{filePath}'. Falling back to filler texture. {ex.Message}");
                return AssignFillerTexture(filePath);
            }
        }

        public override bool Import3rdParty(string filePath, object? importOptions)
        {
            bool ok = Load3rdParty(filePath);
            if (!ok)
                return false;

            if (importOptions is XREngine.Data.XRTexture2DImportOptions options)
            {
                AutoGenerateMipmaps = options.AutoGenerateMipmaps;
                Resizable = options.Resizable;
            }

            return true;
        }

        public static EnumeratorJob ScheduleLoadJob(
            string filePath,
            XRTexture2D? texture = null,
            Action<XRTexture2D>? onFinished = null,
            Action<Exception>? onError = null,
            Action? onCanceled = null,
            Action<float>? onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path must be provided.", nameof(filePath));

            XRTexture2D target = texture ?? new XRTexture2D();
            target.FilePath ??= filePath;
            if (string.IsNullOrWhiteSpace(target.Name))
                target.Name = Path.GetFileNameWithoutExtension(filePath);

            // IMPORTANT: Do not rely on Engine.Jobs to *start* the disk load.
            // The engine may recreate the JobManager during startup (ConfigureJobManager),
            // which can cancel or drop queued jobs. We kick off the IO immediately on the
            // thread pool and only use the job system as an optional tracker.
            Task loadTask = Task.Run(() =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        onCanceled?.Invoke();
                        return;
                    }

                    Debug.Out($"[TextureLoadJob] Loading texture from disk: {filePath}");
                    bool loadSuccess = target.Load3rdParty(filePath);
                    Debug.Out($"[TextureLoadJob] Load3rdParty returned {loadSuccess} for: {filePath}");
                    onProgress?.Invoke(0.5f);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        onCanceled?.Invoke();
                        return;
                    }

                    // Schedule GPU upload but don't block on it - swap tasks may not run yet.
                    ScheduleGpuUpload(target, cancellationToken, () =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            onCanceled?.Invoke();
                            return;
                        }

                        onProgress?.Invoke(1.0f);
                        onFinished?.Invoke(target);
                    });
                }
                catch (OperationCanceledException)
                {
                    onCanceled?.Invoke();
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex);
                }
            }, cancellationToken);

            IEnumerable Routine()
            {
                yield return loadTask;
            }

            return Engine.Jobs.Schedule(
                Routine(),
                progress: null,
                completed: null,
                error: null,
                canceled: null,
                progressWithPayload: null,
                cancellationToken: CancellationToken.None);
        }

        public static EnumeratorJob SchedulePreviewJob(
            string filePath,
            XRTexture2D? texture = null,
            uint maxPreviewSize = 128,
            Action<XRTexture2D>? onFinished = null,
            Action<Exception>? onError = null,
            Action? onCanceled = null,
            Action<float>? onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path must be provided.", nameof(filePath));

            XRTexture2D target = texture ?? new XRTexture2D();
            target.FilePath ??= filePath;
            if (string.IsNullOrWhiteSpace(target.Name))
                target.Name = Path.GetFileNameWithoutExtension(filePath);

            Task previewTask = Task.Run(() =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        onCanceled?.Invoke();
                        return;
                    }

                    bool hasPreview = false;
                    bool isAssetFile = HasAssetExtension(filePath);
                    bool looksLikeTextureAsset = isAssetFile && LooksLikeTextureAssetFile(filePath);

                    if (looksLikeTextureAsset)
                        hasPreview = TryLoadPreviewFromTextureAsset(filePath, target, maxPreviewSize);

                    if (!hasPreview)
                    {
                        if (!isAssetFile)
                        {
                            LoadPreviewFrom3rdParty(filePath, target, maxPreviewSize);
                            hasPreview = true;
                        }
                        else
                        {
                            AssignPlaceholderPreview(target);
                        }
                    }

                    onProgress?.Invoke(0.5f);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        onCanceled?.Invoke();
                        return;
                    }

                    ScheduleGpuUpload(target, cancellationToken, () =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            onCanceled?.Invoke();
                            return;
                        }

                        onProgress?.Invoke(1.0f);
                        onFinished?.Invoke(target);
                    });
                }
                catch (OperationCanceledException)
                {
                    onCanceled?.Invoke();
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex);
                }
            }, cancellationToken);

            IEnumerable Routine()
            {
                yield return previewTask;
            }

            return Engine.Jobs.Schedule(
                Routine(),
                progress: null,
                completed: null,
                error: null,
                canceled: null,
                progressWithPayload: null,
                cancellationToken: CancellationToken.None);
        }

        /// <summary>
        /// Schedules a GPU upload for the texture. The upload will happen on a GL-safe thread
        /// (either immediately if on the render thread, or at the next swap point).
        /// This does NOT block - the onCompleted callback is invoked when the upload finishes.
        /// </summary>
        private static void ScheduleGpuUpload(XRTexture2D texture, CancellationToken cancellationToken, Action? onCompleted = null)
        {
            void UploadAction()
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                try
                {
                    texture.ShouldLoadDataFromInternalPBO = true;

                    var mipmaps = texture.Mipmaps;
                    uint w = mipmaps?.Length > 0 ? mipmaps[0].Width : 0;
                    uint h = mipmaps?.Length > 0 ? mipmaps[0].Height : 0;
                    Debug.Out($"[UploadMipmaps] Starting upload for '{texture.Name}' ({w}x{h}), {mipmaps?.Length ?? 0} mipmaps, IsRenderThread={Engine.IsRenderThread}");

                    if (mipmaps is not null && mipmaps.Length > 0)
                    {
                        for (int i = 0; i < mipmaps.Length; ++i)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                return;
                            texture.LoadFromPBO(i);
                        }
                    }

                    texture.Generate();
                    texture.PushData();
                    Debug.Out($"[UploadMipmaps] Completed upload for '{texture.Name}'");

                    onCompleted?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UploadMipmaps] Exception during upload for '{texture.Name}': {ex.Message}");
                }
            }

            // GPU upload must happen on a thread that owns an active graphics context.
            if (Engine.IsRenderThread)
            {
                UploadAction();
            }
            else
            {
                Engine.EnqueueMainThreadTask(UploadAction);
            }
        }

        private static bool HasAssetExtension(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            return !string.IsNullOrWhiteSpace(extension)
                && extension.Equals($".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase);
        }

        private bool AssignFillerTexture(string filePath)
        {
            AssetDiagnostics.RecordMissingAsset(filePath, nameof(XRTexture2D), $"{nameof(XRTexture2D)}.{nameof(Load3rdParty)}");

            Mipmaps = [new Mipmap2D(new MagickImage(FillerImage))];
            AutoGenerateMipmaps = true;
            Resizable = true;
            return false;
        }

        private const string AssetTypeYamlKey = "__assetType";

        private static bool LooksLikeTextureAssetFile(string filePath)
        {
            try
            {
                using StreamReader reader = new(filePath);
                var yaml = new YamlStream();
                yaml.Load(reader);

                if (yaml.Documents.Count == 0)
                    return false;

                if (yaml.Documents[0].RootNode is not YamlMappingNode mapping)
                    return false;

                string tagText = mapping.Tag.ToString();
                if (!string.IsNullOrWhiteSpace(tagText)
                    && tagText.Contains(nameof(XRTexture2D), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (TryReadAssetTypeHint(mapping, out string? typeHint)
                    && MatchesTextureTypeIdentifier(typeHint))
                {
                    return true;
                }

                int inspectedKeys = 0;
                const int MaxKeysToInspect = 128;

                foreach (var kvp in mapping.Children)
                {
                    if (inspectedKeys++ >= MaxKeysToInspect)
                        break;

                    if (kvp.Key is not YamlScalarNode keyNode)
                        continue;

                    string? key = keyNode.Value;
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    if (string.Equals(key, "SourceAsset", StringComparison.OrdinalIgnoreCase))
                        break;

                    if (IsTextureFieldName(key))
                        return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException)
            {
                Debug.Out($"Failed to inspect asset file '{filePath}' while determining preview type. {ex.Message}");
            }

            return false;
        }

        private static bool TryReadAssetTypeHint(YamlMappingNode mapping, out string? typeHint)
        {
            typeHint = null;
            foreach (var kvp in mapping.Children)
            {
                if (kvp.Key is not YamlScalarNode keyNode)
                    continue;

                if (!string.Equals(keyNode.Value, AssetTypeYamlKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (kvp.Value is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
                    continue;

                typeHint = scalar.Value;
                return true;
            }

            typeHint = null;
            return false;
        }

        private static bool MatchesTextureTypeIdentifier(string? identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
                return false;

            Type textureType = typeof(XRTexture2D);
            string? fullName = textureType.FullName;
            if (!string.IsNullOrWhiteSpace(fullName)
                && identifier.Contains(fullName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return identifier.Contains(textureType.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTextureFieldName(string key)
            => string.Equals(key, "_mipmaps", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "Mipmaps", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "AutoGenerateMipmaps", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "SizedInternalFormat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "MagFilter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "MinFilter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "UWrap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "VWrap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "FrameBufferAttachment", StringComparison.OrdinalIgnoreCase);

        private static void LoadPreviewFrom3rdParty(string filePath, XRTexture2D target, uint maxPreviewSize)
        {
            try
            {
                using MagickImage previewImage = new(filePath);
                ResizePreviewIfNeeded(previewImage, maxPreviewSize);
                target.Mipmaps = [new Mipmap2D(previewImage)];
                target.AutoGenerateMipmaps = false;
                target.Resizable = true;
                target.SizedInternalFormat = ESizedInternalFormat.Rgba8;
            }
            catch (MagickException ex)
            {
                Debug.LogWarning($"Failed to load preview image '{filePath}': {ex.Message}. Using placeholder preview instead.");
                AssignPlaceholderPreview(target);
            }
        }

        private static bool TryLoadPreviewFromTextureAsset(string filePath, XRTexture2D target, uint maxPreviewSize)
            => TryLoadTextureAsset(filePath, target, deepCopy: false)
                && TryPreparePreviewFromLoadedTexture(target, maxPreviewSize);

        private static bool TryLoadTextureAsset(string filePath, XRTexture2D target, bool deepCopy)
        {
            var assetManager = Engine.Assets;
            if (assetManager is null)
                return false;

            try
            {
                XRTexture2D? loadedTexture = assetManager.Load<XRTexture2D>(filePath);
                if (loadedTexture is null)
                    return false;

                CopyTextureData(loadedTexture, target, deepCopy);
                return true;
            }
            catch (Exception ex) when (IsTextureAssetLoadFailure(ex))
            {
                Debug.Out($"Failed to load texture asset '{filePath}' while preparing texture data: {ex.Message}");
                return false;
            }
        }

        private static bool TryPreparePreviewFromLoadedTexture(XRTexture2D target, uint maxPreviewSize)
        {
            var sourceMipmaps = target.Mipmaps;
            if (sourceMipmaps is null || sourceMipmaps.Length == 0)
                return false;

            var baseMipmap = sourceMipmaps[0];
            if (baseMipmap is null || !baseMipmap.HasData())
                return false;

            try
            {
                using MagickImage baseImage = baseMipmap.GetImage();
                ResizePreviewIfNeeded(baseImage, maxPreviewSize);
                target.Mipmaps = [new Mipmap2D(baseImage)];
                return true;
            }
            catch (MagickException)
            {
                return false;
            }
        }

        private static void CopyTextureData(XRTexture2D source, XRTexture2D target, bool deepCopy)
        {
            var sourceMipmaps = source.Mipmaps;
            if (sourceMipmaps is null || sourceMipmaps.Length == 0)
            {
                target.Mipmaps = [];
            }
            else
            {
                Mipmap2D[] copies = new Mipmap2D[sourceMipmaps.Length];
                for (int i = 0; i < sourceMipmaps.Length; i++)
                {
                    var mip = sourceMipmaps[i];
                    copies[i] = mip is null ? new Mipmap2D() : mip.Clone(cloneImage: deepCopy);
                }
                target.Mipmaps = copies;
            }

            CopyTextureSettings(source, target);
        }

        private static void AssignPlaceholderPreview(XRTexture2D target)
        {
            using MagickImage filler = (MagickImage)FillerImage.Clone();
            target.Mipmaps = [new Mipmap2D(filler)];
            target.AutoGenerateMipmaps = false;
            target.Resizable = true;
            target.SizedInternalFormat = ESizedInternalFormat.Rgba8;
        }

        private static void CopyTextureSettings(XRTexture2D source, XRTexture2D target)
        {
            target.AutoGenerateMipmaps = source.AutoGenerateMipmaps;
            target.Resizable = source.Resizable;
            target.SizedInternalFormat = source.SizedInternalFormat;
            target.MagFilter = source.MagFilter;
            target.MinFilter = source.MinFilter;
            target.UWrap = source.UWrap;
            target.VWrap = source.VWrap;
            target.LodBias = source.LodBias;
        }

        private static bool IsTextureAssetLoadFailure(Exception ex)
            => ex is YamlException or MagickException or InvalidOperationException;

        private static void ResizePreviewIfNeeded(MagickImage previewImage, uint maxPreviewSize)
        {
            if (previewImage is null || maxPreviewSize == 0)
                return;

            uint width = previewImage.Width;
            uint height = previewImage.Height;
            uint largest = Math.Max(width, height);
            if (largest == 0 || largest <= maxPreviewSize)
                return;

            double scale = maxPreviewSize / (double)largest;
            uint scaledWidth = Math.Max(1u, (uint)Math.Round(width * scale));
            uint scaledHeight = Math.Max(1u, (uint)Math.Round(height * scale));
            previewImage.Resize(scaledWidth, scaledHeight);
        }

        public static Mipmap2D[] GetMipmapsFromImage(MagickImage image)
        {
            Mipmap2D[] mips = new Mipmap2D[GetSmallestMipmapLevel(image.Width, image.Height)];
            mips[0] = new Mipmap2D(image);
            uint w = image.Width;
            uint h = image.Height;
            for (int i = 1; i < mips.Length; ++i)
            {
                var clone = image.Clone();
                clone.Resize(w >> i, h >> i);
                mips[i] = new Mipmap2D(clone as MagickImage);
            }
            return mips;
        }

        private static MagickImage? _fillerImage = null;
        public static MagickImage FillerImage => _fillerImage ??= GetFillerBitmap();

        private static MagickImage GetFillerBitmap()
        {
            string path = Path.Combine(Engine.GameSettings.TexturesFolder, "Filler.png");
            if (File.Exists(path))
                return new MagickImage(path);
            else
            {
                const int squareExtent = 4;
                const int dim = squareExtent * 2;

                // Create a checkerboard pattern image without using bitmap
                MagickImage img = new(MagickColors.Blue, dim, dim);
                img.Draw(new Drawables()
                    .FillColor(MagickColors.Red)
                    .Rectangle(0, 0, squareExtent, squareExtent)
                    .Rectangle(squareExtent, squareExtent, dim, dim));
                return img;
            }
        }

        /// <summary>
        /// Derives ESizedInternalFormat from EPixelInternalFormat for proper texture storage allocation.
        /// This is critical for depth/stencil textures where the wrong format causes FBO incomplete errors.
        /// </summary>
        private static ESizedInternalFormat DeriveESizedInternalFormat(EPixelInternalFormat internalFormat)
            => internalFormat switch
            {
                // Red channel formats
                EPixelInternalFormat.R8 => ESizedInternalFormat.R8,
                EPixelInternalFormat.R8SNorm => ESizedInternalFormat.R8Snorm,
                EPixelInternalFormat.R16 => ESizedInternalFormat.R16,
                EPixelInternalFormat.R16SNorm => ESizedInternalFormat.R16Snorm,
                EPixelInternalFormat.R16f => ESizedInternalFormat.R16f,
                EPixelInternalFormat.R32f => ESizedInternalFormat.R32f,
                EPixelInternalFormat.R8i => ESizedInternalFormat.R8i,
                EPixelInternalFormat.R8ui => ESizedInternalFormat.R8ui,
                EPixelInternalFormat.R16i => ESizedInternalFormat.R16i,
                EPixelInternalFormat.R16ui => ESizedInternalFormat.R16ui,
                EPixelInternalFormat.R32i => ESizedInternalFormat.R32i,
                EPixelInternalFormat.R32ui => ESizedInternalFormat.R32ui,

                // RG channel formats
                EPixelInternalFormat.RG8 => ESizedInternalFormat.Rg8,
                EPixelInternalFormat.RG8SNorm => ESizedInternalFormat.Rg8Snorm,
                EPixelInternalFormat.RG16 => ESizedInternalFormat.Rg16,
                EPixelInternalFormat.RG16SNorm => ESizedInternalFormat.Rg16Snorm,
                EPixelInternalFormat.RG16f => ESizedInternalFormat.Rg16f,
                EPixelInternalFormat.RG32f => ESizedInternalFormat.Rg32f,
                EPixelInternalFormat.RG8i => ESizedInternalFormat.Rg8i,
                EPixelInternalFormat.RG8ui => ESizedInternalFormat.Rg8ui,
                EPixelInternalFormat.RG16i => ESizedInternalFormat.Rg16i,
                EPixelInternalFormat.RG16ui => ESizedInternalFormat.Rg16ui,
                EPixelInternalFormat.RG32i => ESizedInternalFormat.Rg32i,
                EPixelInternalFormat.RG32ui => ESizedInternalFormat.Rg32ui,

                // RGB formats
                EPixelInternalFormat.R3G3B2 => ESizedInternalFormat.R3G3B2,
                EPixelInternalFormat.Rgb4 => ESizedInternalFormat.Rgb4,
                EPixelInternalFormat.Rgb5 => ESizedInternalFormat.Rgb5,
                EPixelInternalFormat.Rgb8 => ESizedInternalFormat.Rgb8,
                EPixelInternalFormat.Rgb8SNorm => ESizedInternalFormat.Rgb8Snorm,
                EPixelInternalFormat.Rgb10 => ESizedInternalFormat.Rgb10,
                EPixelInternalFormat.Rgb12 => ESizedInternalFormat.Rgb12,
                EPixelInternalFormat.Rgb16SNorm => ESizedInternalFormat.Rgb16Snorm,
                EPixelInternalFormat.Srgb8 => ESizedInternalFormat.Srgb8,
                EPixelInternalFormat.Rgb16f => ESizedInternalFormat.Rgb16f,
                EPixelInternalFormat.Rgb32f => ESizedInternalFormat.Rgb32f,
                EPixelInternalFormat.R11fG11fB10f => ESizedInternalFormat.R11fG11fB10f,
                EPixelInternalFormat.Rgb9E5 => ESizedInternalFormat.Rgb9E5,
                EPixelInternalFormat.Rgb8i => ESizedInternalFormat.Rgb8i,
                EPixelInternalFormat.Rgb8ui => ESizedInternalFormat.Rgb8ui,
                EPixelInternalFormat.Rgb16i => ESizedInternalFormat.Rgb16i,
                EPixelInternalFormat.Rgb16ui => ESizedInternalFormat.Rgb16ui,
                EPixelInternalFormat.Rgb32i => ESizedInternalFormat.Rgb32i,
                EPixelInternalFormat.Rgb32ui => ESizedInternalFormat.Rgb32ui,

                // RGBA formats
                EPixelInternalFormat.Rgba2 => ESizedInternalFormat.Rgba2,
                EPixelInternalFormat.Rgba4 => ESizedInternalFormat.Rgba4,
                EPixelInternalFormat.Rgb5A1 => ESizedInternalFormat.Rgb5A1,
                EPixelInternalFormat.Rgba8 => ESizedInternalFormat.Rgba8,
                EPixelInternalFormat.Rgba8SNorm => ESizedInternalFormat.Rgba8Snorm,
                EPixelInternalFormat.Rgb10A2 => ESizedInternalFormat.Rgb10A2,
                EPixelInternalFormat.Rgba12 => ESizedInternalFormat.Rgba12,
                EPixelInternalFormat.Rgba16 => ESizedInternalFormat.Rgba16,
                EPixelInternalFormat.Srgb8Alpha8 => ESizedInternalFormat.Srgb8Alpha8,
                EPixelInternalFormat.Rgba16f => ESizedInternalFormat.Rgba16f,
                EPixelInternalFormat.Rgba32f => ESizedInternalFormat.Rgba32f,
                EPixelInternalFormat.Rgba8i => ESizedInternalFormat.Rgba8i,
                EPixelInternalFormat.Rgba8ui => ESizedInternalFormat.Rgba8ui,
                EPixelInternalFormat.Rgba16i => ESizedInternalFormat.Rgba16i,
                EPixelInternalFormat.Rgba16ui => ESizedInternalFormat.Rgba16ui,
                EPixelInternalFormat.Rgba32i => ESizedInternalFormat.Rgba32i,
                EPixelInternalFormat.Rgba32ui => ESizedInternalFormat.Rgba32ui,

                // Depth formats - CRITICAL for shadow maps!
                EPixelInternalFormat.DepthComponent16 => ESizedInternalFormat.DepthComponent16,
                EPixelInternalFormat.DepthComponent24 => ESizedInternalFormat.DepthComponent24,
                EPixelInternalFormat.DepthComponent32f => ESizedInternalFormat.DepthComponent32f,

                // Depth-stencil formats
                EPixelInternalFormat.Depth24Stencil8 => ESizedInternalFormat.Depth24Stencil8,
                EPixelInternalFormat.Depth32fStencil8 => ESizedInternalFormat.Depth32fStencil8,

                // Stencil formats
                EPixelInternalFormat.StencilIndex8 => ESizedInternalFormat.StencilIndex8,

                // Default fallback for unsized/legacy formats
                _ => ESizedInternalFormat.Rgba32f,
            };

        private ESizedInternalFormat _sizedInternalFormat = ESizedInternalFormat.Rgba32f;
        private ETexMagFilter _magFilter = ETexMagFilter.Nearest;
        private ETexMinFilter _minFilter = ETexMinFilter.Nearest;
        private ETexWrapMode _uWrap = ETexWrapMode.Repeat;
        private ETexWrapMode _vWrap = ETexWrapMode.Repeat;
        private float _lodBias = 0.0f;
        private bool _resizable = true;
        private bool _exclusiveSharing = true;
        private GrabPassInfo? _grabPass;

        public override bool IsResizeable => Resizable;

        /// <summary>
        /// If false, calling resize will do nothing.
        /// Useful for repeating textures that must always be a certain size or textures that never need to be dynamically resized during the game.
        /// False by default.
        /// </summary>
        public bool Resizable
        {
            get => _resizable;
            set => SetField(ref _resizable, value);
        }

        public override uint MaxDimension => Math.Max(Width, Height);

        public XRTexture2D(Task<Image<Rgba32>?> loadTask)
        {
            Mipmaps = [new Mipmap2D(loadTask)];
        }
        public XRTexture2D(uint width, uint height, ColorF4 color)
        {
            Mipmaps = [new Mipmap2D(width, height, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true)
            {
                Data = new DataSource(ColorToBytes(width, height, color))
            }];
        }

        private static byte[] ColorToBytes(uint width, uint height, ColorF4 color)
        {
            byte[] data = new byte[width * height * 4];
            for (int i = 0; i < data.Length; i += 4)
            {
                data[i] = (byte)(color.R * 255);
                data[i + 1] = (byte)(color.G * 255);
                data[i + 2] = (byte)(color.B * 255);
                data[i + 3] = (byte)(color.A * 255);
            }
            return data;
        }

        [MemoryPackConstructor]
        public XRTexture2D() : this(1u, 1u, EPixelInternalFormat.Rgb8, EPixelFormat.Rgb, EPixelType.UnsignedByte, true) { }
        public XRTexture2D(uint width, uint height, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, int mipmapCount)
        {
            // Derive SizedInternalFormat from the pixel internal format for proper texture storage.
            _sizedInternalFormat = DeriveESizedInternalFormat(internalFormat);

            Mipmap2D[] mips = new Mipmap2D[mipmapCount];
            for (uint i = 0; i < mipmapCount; ++i)
            {
                Mipmap2D mipmap = new()
                {
                    InternalFormat = internalFormat,
                    PixelFormat = format,
                    PixelType = type,
                    Width = width,
                    Height = height,
                    Data = new DataSource(AllocateBytes(width, height, format, type))
                };
                width >>= 1;
                height >>= 1;
                mips[i] = mipmap;
            }
            Mipmaps = mips;
        }

        public XRTexture2D(params string[] mipMapPaths)
        {
            Mipmap2D[] mips = new Mipmap2D[mipMapPaths.Length];
            for (int i = 0; i < mipMapPaths.Length; ++i)
            {
                string path = mipMapPaths[i];
                if (path.StartsWith("file://"))
                    path = path[7..];
                try
                {
                    mips[i] = new Mipmap2D(new MagickImage(path));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to load texture from path: {path}{Environment.NewLine}{e.Message}");
                }
            }
            Mipmaps = mips;
        }
        public XRTexture2D(uint width, uint height, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData = false)
        {
            // Derive SizedInternalFormat from the pixel internal format for proper texture storage.
            // This is critical for depth textures (shadow maps) where the wrong sized format causes FBO incomplete.
            _sizedInternalFormat = DeriveESizedInternalFormat(internalFormat);

            Mipmaps = [new Mipmap2D() 
            {
                InternalFormat = internalFormat,
                PixelFormat = format,
                PixelType = type,
                Width = width,
                Height = height,
                Data = allocateData ? new DataSource(AllocateBytes(width, height, format, type)) : null
            }];
        }
        public XRTexture2D(uint width, uint height, params MagickImage?[] mipmaps)
        {
            Mipmap2D[] mips = new Mipmap2D[mipmaps.Length];
            for (int i = 0; i < mipmaps.Length; ++i)
            {
                var image = mipmaps[i];
                image?.Resize(width >> i, height >> i);
                mips[i] = new Mipmap2D(image);
            }
            Mipmaps = mips;
        }
        public XRTexture2D(MagickImage? image)
        {
            Mipmaps = [new Mipmap2D(image)];
        }
        public XRTexture2D(Image<Rgba32> image)
        {
            Mipmaps = [new Mipmap2D(image)];
        }

        private Mipmap2D[] _mipmaps = [];
        public Mipmap2D[] Mipmaps
        {
            get => _mipmaps;
            set => SetField(ref _mipmaps, value);
        }

        public ETexMagFilter MagFilter
        {
            get => _magFilter;
            set => SetField(ref _magFilter, value);
        }
        public ETexMinFilter MinFilter
        {
            get => _minFilter;
            set => SetField(ref _minFilter, value);
        }
        public ETexWrapMode UWrap
        {
            get => _uWrap;
            set => SetField(ref _uWrap, value);
        }
        public ETexWrapMode VWrap
        {
            get => _vWrap;
            set => SetField(ref _vWrap, value);
        }
        public GrabPassInfo? GrabPass
        {
            get => _grabPass;
            set => SetField(ref _grabPass, value);
        }
        public float LodBias
        {
            get => _lodBias;
            set => SetField(ref _lodBias, value);
        }
        public ESizedInternalFormat SizedInternalFormat
        {
            get => _sizedInternalFormat;
            set => SetField(ref _sizedInternalFormat, value);
        }

        public uint Width => Mipmaps.Length > 0 ? Mipmaps[0].Width : 0;
        public uint Height => Mipmaps.Length > 0 ? Mipmaps[0].Height : 0;

        /// <summary>
        /// Set on construction
        /// </summary>
        public bool Rectangle { get; set; } = false;
        public bool MultiSample => MultiSampleCount > 1;

        private uint _multiSampleCount = 1;
        /// <summary>
        /// Set on construction
        /// </summary>
        public uint MultiSampleCount
        {
            get => _multiSampleCount;
            set => SetField(ref _multiSampleCount, value);
        }

        private bool _fixedSampleLocations = true;
        /// <summary>
        /// Specifies whether the image will use identical sample locations 
        /// and the same number of samples for all texels in the image,
        /// and the sample locations will not depend on the internal format or size of the image.
        /// </summary>
        public bool FixedSampleLocations
        {
            get => _fixedSampleLocations;
            set => SetField(ref _fixedSampleLocations, value);
        }

        public bool ExclusiveSharing
        {
            get => _exclusiveSharing;
            set => SetField(ref _exclusiveSharing, value);
        }

        /// <summary>
        /// Resizes the textures stored in memory.
        /// Does nothing if Resizeable is false.
        /// </summary>
        public void Resize(uint width, uint height)
        {
            if (Width == width && Height == height || _mipmaps is null || _mipmaps.Length <= 0)
                return;

            for (int i = 0; i < _mipmaps.Length && width > 0u && height > 0u; ++i)
            {
                if (_mipmaps[i] is null)
                    continue;

                _mipmaps[i]?.Resize(width, height);

                width >>= 1;
                height >>= 1;
            }

            Resized?.Invoke();
        }

    [field: MemoryPackIgnore]
    public event Action? Resized;

        /// <summary>
        /// Generates mipmaps from the base texture.
        /// </summary>
        public void GenerateMipmapsCPU()
        {
            if (_mipmaps is null || _mipmaps.Length <= 0)
                return;

            Mipmaps = GetMipmapsFromImage(_mipmaps[0].GetImage());
        }

        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// </summary>
        /// <param name="name">The name of the texture.</param>
        /// <param name="width">The texture's width.</param>
        /// <param name="height">The texture's height.</param>
        /// <param name="internalFmt">The internal texture storage format.</param>
        /// <param name="format">The format of the texture's pixels.</param>
        /// <param name="pixelType">How pixels are stored.</param>
        /// <param name="bufAttach">Where to attach to the framebuffer for rendering to.</param>
        /// <returns>A new 2D texture reference.</returns>
        public static XRTexture2D CreateFrameBufferTexture(uint width, uint height,
            EPixelInternalFormat internalFmt, EPixelFormat format, EPixelType type, EFrameBufferAttachment bufAttach)
            => new(width, height, internalFmt, format, type, false)
            {
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
                FrameBufferAttachment = bufAttach,
            };

        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="bounds"></param>
        /// <param name="internalFormat"></param>
        /// <param name="format"></param>
        /// <param name="pixelType"></param>
        /// <returns></returns>
        public static XRTexture2D CreateFrameBufferTexture(IVector2 bounds, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type)
            => CreateFrameBufferTexture((uint)bounds.X, (uint)bounds.Y, internalFormat, format, type);
        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// </summary>
        /// <param name="name">The name of the texture.</param>
        /// <param name="width">The texture's width.</param>
        /// <param name="height">The texture's height.</param>
        /// <param name="internalFmt">The internal texture storage format.</param>
        /// <param name="format">The format of the texture's pixels.</param>
        /// <param name="pixelType">How pixels are stored.</param>
        /// <returns>A new 2D texture reference.</returns>
        public static XRTexture2D CreateFrameBufferTexture(uint width, uint height, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type)
            => new(width, height, internalFormat, format, type, false)
            {
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
            };

        [MemoryPackIgnore]
        private XRDataBuffer? _pbo;

        public bool ShouldLoadDataFromInternalPBO
        {
            get => _pbo != null;
            set
            {
                if (value)
                {
                    if (_pbo == null)
                    {
                        _pbo = new XRDataBuffer(EBufferTarget.PixelUnpackBuffer, true)
                        {
                            // Must specify Write access for mapping to copy mipmap data to GPU
                            RangeFlags = EBufferMapRangeFlags.Write,
                            Usage = EBufferUsage.StreamDraw
                        };
                        _pbo.Generate();
                    }
                }
                else
                {
                    if (_pbo != null)
                    {
                        _pbo.Destroy();
                        _pbo = null;
                    }
                }
            }
        }

        public override bool HasAlphaChannel
            => Mipmaps.Any(HasAlpha);

        public override Vector3 WidthHeightDepth
            => new(Width, Height, 1);

        private static bool HasAlpha(Mipmap2D x) => x.PixelFormat switch
        {
            EPixelFormat.Bgra or
            EPixelFormat.Rgba or
            EPixelFormat.LuminanceAlpha => true,
            _ => false,
        };

        public unsafe void LoadFromPBO(int mipIndex)
        {
            if (_pbo is null)
                return;

            if (Mipmaps is null || mipIndex < 0 || mipIndex >= Mipmaps.Length)
                return;

            var mip = Mipmaps[mipIndex];
            var data = mip.Data;
            if (data is null || data.Length == 0)
                return;

            // Allocate the PBO to the size of the mipmap data before mapping
            _pbo.Allocate((uint)data.Length, 1);
            _pbo.PushData();

            _pbo.MapBufferData();
            _pbo.SetDataPointer(data.Address);
            _pbo.UnmapBufferData();

            mip.StreamingPBO = _pbo;
        }

        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// Resizes the texture to a scale of the current viewport size.
        /// </summary>
        /// <param name="resizeScale"></param>
        /// <param name="readBuffer"></param>
        /// <param name="colorBit"></param>
        /// <param name="depthBit"></param>
        /// <param name="stencilBit"></param>
        /// <param name="linearFilter"></param>
        /// <returns></returns>
        public static XRTexture2D CreateGrabPassTextureResized(
            float resizeScale = 1.0f,
            EReadBufferMode readBuffer = EReadBufferMode.ColorAttachment0,
            bool colorBit = true,
            bool depthBit = false,
            bool stencilBit = false,
            bool linearFilter = true)
        {
            XRTexture2D t = CreateFrameBufferTexture(1, 1, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.GrabPass = new GrabPassInfo(t, readBuffer, colorBit, depthBit, stencilBit, linearFilter, true, resizeScale);
            return t;
        }

        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// Keeps the texture at the same size as when it was created.
        /// </summary>
        /// <param name="w"></param>
        /// <param name="h"></param>
        /// <param name="readBuffer"></param>
        /// <param name="colorBit"></param>
        /// <param name="depthBit"></param>
        /// <param name="stencilBit"></param>
        /// <param name="linearFilter"></param>
        /// <returns></returns>
        public static XRTexture2D CreateGrabPassTextureStatic(
            uint w,
            uint h,
            EReadBufferMode readBuffer = EReadBufferMode.ColorAttachment0,
            bool colorBit = true,
            bool depthBit = false,
            bool stencilBit = false,
            bool linearFilter = true)
        {
            XRTexture2D t = CreateFrameBufferTexture(w, h, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
            t.MinFilter = ETexMinFilter.Linear;
            t.MagFilter = ETexMagFilter.Linear;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.GrabPass = new GrabPassInfo(t, readBuffer, colorBit, depthBit, stencilBit, linearFilter, false, 1.0f);
            return t;
        }

        [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
        [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
        void ICookedBinarySerializable.WriteCookedBinary(CookedBinaryWriter writer)
        {
            WriteTextureAssetBase(writer);

            writer.WriteValue(SamplerName);
            writer.WriteValue(FrameBufferAttachment);
            writer.WriteValue(MinLOD);
            writer.WriteValue(MaxLOD);
            writer.WriteValue(LargestMipmapLevel);
            writer.WriteValue(SmallestAllowedMipmapLevel);
            writer.WriteValue(AutoGenerateMipmaps);
            writer.WriteValue(AlphaAsTransparency);
            writer.WriteValue(InternalCompression);

            writer.WriteValue(MagFilter);
            writer.WriteValue(MinFilter);
            writer.WriteValue(UWrap);
            writer.WriteValue(VWrap);
            writer.WriteValue(Rectangle);
            writer.WriteValue(Resizable);
            writer.WriteValue(MultiSampleCount);
            writer.WriteValue(FixedSampleLocations);
            writer.WriteValue(ExclusiveSharing);
            writer.WriteValue(LodBias);
            writer.WriteValue(SizedInternalFormat);

            WriteGrabPass(writer, GrabPass);
            WriteMipmaps(writer, Mipmaps);
        }

        [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
        [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
        long ICookedBinarySerializable.CalculateCookedBinarySize()
        {
            long size = CalculateTextureAssetBaseSize();

            size += CookedBinarySerializer.CalculateSize(SamplerName);
            size += CookedBinarySerializer.CalculateSize(FrameBufferAttachment);
            size += CookedBinarySerializer.CalculateSize(MinLOD);
            size += CookedBinarySerializer.CalculateSize(MaxLOD);
            size += CookedBinarySerializer.CalculateSize(LargestMipmapLevel);
            size += CookedBinarySerializer.CalculateSize(SmallestAllowedMipmapLevel);
            size += CookedBinarySerializer.CalculateSize(AutoGenerateMipmaps);
            size += CookedBinarySerializer.CalculateSize(AlphaAsTransparency);
            size += CookedBinarySerializer.CalculateSize(InternalCompression);

            size += CookedBinarySerializer.CalculateSize(MagFilter);
            size += CookedBinarySerializer.CalculateSize(MinFilter);
            size += CookedBinarySerializer.CalculateSize(UWrap);
            size += CookedBinarySerializer.CalculateSize(VWrap);
            size += CookedBinarySerializer.CalculateSize(Rectangle);
            size += CookedBinarySerializer.CalculateSize(Resizable);
            size += CookedBinarySerializer.CalculateSize(MultiSampleCount);
            size += CookedBinarySerializer.CalculateSize(FixedSampleLocations);
            size += CookedBinarySerializer.CalculateSize(ExclusiveSharing);
            size += CookedBinarySerializer.CalculateSize(LodBias);
            size += CookedBinarySerializer.CalculateSize(SizedInternalFormat);

            size += CalculateGrabPassSize(GrabPass);
            size += CalculateMipmapSize(Mipmaps);

            return size;
        }

        [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
        [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
        void ICookedBinarySerializable.ReadCookedBinary(CookedBinaryReader reader)
        {
            ReadTextureAssetBase(reader);

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

            GrabPass = ReadGrabPass(reader, this);
            Mipmaps = ReadMipmaps(reader);
        }

        [RequiresUnreferencedCode("Calls XREngine.Core.Files.CookedBinaryWriter.WriteValue(Object)")]
        [RequiresDynamicCode("Calls XREngine.Core.Files.CookedBinaryWriter.WriteValue(Object)")]
        private static void WriteMipmaps(CookedBinaryWriter writer, Mipmap2D[] mipmaps)
        {
            writer.WriteValue(mipmaps?.Length ?? 0);
            if (mipmaps is null)
                return;

            foreach (var mip in mipmaps)
            {
                writer.WriteValue(mip.Width);
                writer.WriteValue(mip.Height);
                writer.WriteValue(mip.InternalFormat);
                writer.WriteValue(mip.PixelFormat);
                writer.WriteValue(mip.PixelType);
                writer.WriteValue(mip.Data is null ? null : mip.Data.GetBytes());
            }
        }

        [RequiresUnreferencedCode("Calls XREngine.Core.Files.CookedBinaryReader.ReadValue<T>()")]
        [RequiresDynamicCode("Calls XREngine.Core.Files.CookedBinaryReader.ReadValue<T>()")]
        private static Mipmap2D[] ReadMipmaps(CookedBinaryReader reader)
        {
            int mipCount = ReadStructOrDefault(reader, 0);
            if (mipCount <= 0)
                return [];

            Mipmap2D[] mipmaps = new Mipmap2D[mipCount];
            for (int i = 0; i < mipCount; i++)
            {
                uint width = ReadStructOrDefault(reader, 0u);
                uint height = ReadStructOrDefault(reader, 0u);
                var internalFormat = ReadStructOrDefault(reader, EPixelInternalFormat.Rgba8);
                var pixelFormat = ReadStructOrDefault(reader, EPixelFormat.Rgba);
                var pixelType = ReadStructOrDefault(reader, EPixelType.UnsignedByte);
                byte[]? bytes = reader.ReadValue<byte[]>();

                Mipmap2D mip = new()
                {
                    Width = width,
                    Height = height,
                    InternalFormat = internalFormat,
                    PixelFormat = pixelFormat,
                    PixelType = pixelType,
                    Data = bytes is null ? null : new DataSource(bytes)
                };
                mipmaps[i] = mip;
            }

            return mipmaps;
        }

        [RequiresUnreferencedCode("Calls XREngine.Core.Files.CookedBinaryWriter.WriteValue(Object)")]
        [RequiresDynamicCode("Calls XREngine.Core.Files.CookedBinaryWriter.WriteValue(Object)")]
        private static void WriteGrabPass(CookedBinaryWriter writer, GrabPassInfo? grabPass)
        {
            writer.WriteValue(grabPass is not null);
            if (grabPass is null)
                return;

            writer.WriteValue(grabPass.ReadBuffer);
            writer.WriteValue(grabPass.ColorBit);
            writer.WriteValue(grabPass.DepthBit);
            writer.WriteValue(grabPass.StencilBit);
            writer.WriteValue(grabPass.LinearFilter);
            writer.WriteValue(grabPass.ResizeToFit);
            writer.WriteValue(grabPass.ResizeScale);
        }

        [RequiresUnreferencedCode("Calls XREngine.Rendering.XRTexture2D.ReadStructOrDefault<T>(CookedBinaryReader, T)")]
        [RequiresDynamicCode("Calls XREngine.Rendering.XRTexture2D.ReadStructOrDefault<T>(CookedBinaryReader, T)")]
        private static GrabPassInfo? ReadGrabPass(CookedBinaryReader reader, XRTexture2D owner)
        {
            bool hasGrabPass = ReadStructOrDefault(reader, false);
            if (!hasGrabPass)
                return null;

            var readBuffer = ReadStructOrDefault(reader, EReadBufferMode.ColorAttachment0);
            bool colorBit = ReadStructOrDefault(reader, true);
            bool depthBit = ReadStructOrDefault(reader, false);
            bool stencilBit = ReadStructOrDefault(reader, false);
            bool linearFilter = ReadStructOrDefault(reader, true);
            bool resizeToFit = ReadStructOrDefault(reader, true);
            float resizeScale = ReadStructOrDefault(reader, 1.0f);

            return new GrabPassInfo(owner, readBuffer, colorBit, depthBit, stencilBit, linearFilter, resizeToFit, resizeScale);
        }

        [RequiresUnreferencedCode("Calls XREngine.Core.Files.CookedBinaryReader.ReadValue<T>()")]
        [RequiresDynamicCode("Calls XREngine.Core.Files.CookedBinaryReader.ReadValue<T>()")]
        private static T ReadStructOrDefault<T>(CookedBinaryReader reader, T fallback) where T : struct
        {
            T? value = reader.ReadValue<T?>();
            return value ?? fallback;
        }

        [RequiresUnreferencedCode("Calls XREngine.Core.Files.CookedBinarySerializer.CalculateSize(Object, CookedBinarySerializationCallbacks)")]
        [RequiresDynamicCode("Calls XREngine.Core.Files.CookedBinarySerializer.CalculateSize(Object, CookedBinarySerializationCallbacks)")]
        private static long CalculateGrabPassSize(GrabPassInfo? grabPass)
        {
            long size = CookedBinarySerializer.CalculateSize(grabPass is not null);
            if (grabPass is null)
                return size;

            size += CookedBinarySerializer.CalculateSize(grabPass.ReadBuffer);
            size += CookedBinarySerializer.CalculateSize(grabPass.ColorBit);
            size += CookedBinarySerializer.CalculateSize(grabPass.DepthBit);
            size += CookedBinarySerializer.CalculateSize(grabPass.StencilBit);
            size += CookedBinarySerializer.CalculateSize(grabPass.LinearFilter);
            size += CookedBinarySerializer.CalculateSize(grabPass.ResizeToFit);
            size += CookedBinarySerializer.CalculateSize(grabPass.ResizeScale);
            return size;
        }

        [RequiresUnreferencedCode("Calls XREngine.Core.Files.CookedBinarySerializer.CalculateSize(Object, CookedBinarySerializationCallbacks)")]
        [RequiresDynamicCode("Calls XREngine.Core.Files.CookedBinarySerializer.CalculateSize(Object, CookedBinarySerializationCallbacks)")]
        private static long CalculateMipmapSize(Mipmap2D[] mipmaps)
        {
            long size = CookedBinarySerializer.CalculateSize(mipmaps?.Length ?? 0);
            if (mipmaps is null)
                return size;

            foreach (var mip in mipmaps)
            {
                size += CookedBinarySerializer.CalculateSize(mip.Width);
                size += CookedBinarySerializer.CalculateSize(mip.Height);
                size += CookedBinarySerializer.CalculateSize(mip.InternalFormat);
                size += CookedBinarySerializer.CalculateSize(mip.PixelFormat);
                size += CookedBinarySerializer.CalculateSize(mip.PixelType);

                byte[]? bytes = mip.Data is null ? null : mip.Data.GetBytes();
                size += CookedBinarySerializer.CalculateSize(bytes);
            }

            return size;
        }
    }
}
