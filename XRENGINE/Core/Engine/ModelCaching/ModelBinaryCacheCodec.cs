using System;
using System.Buffers;
using System.IO;
using System.IO.Hashing;
using XREngine.Core.Files;
using XREngine.Core.Files.Caching;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene.Prefabs;

namespace XREngine.ModelCaching
{
    /// <summary>
    /// Establishes exclusive cache ownership for imported model prefabs.
    /// Phase 3 validates binary manifests without falling through to YAML. Live prefab hydration
    /// and publication remain disabled until cooked model sections are available.
    /// </summary>
    internal sealed class ModelBinaryCacheCodec : IThirdPartyCacheCodec
    {
        public CacheCodecOwnership GetOwnership(Type assetType)
            => typeof(XRPrefabSource).IsAssignableFrom(assetType)
                ? CacheCodecOwnership.Exclusive
                : CacheCodecOwnership.NotHandled;

        public CacheWriteMode WriteMode => CacheWriteMode.Blocking;

        public string? ResolveDefaultVariantKey(string? explicitVariantKey)
            => explicitVariantKey;

        public XRAsset PrepareForWrite(string cachePath, XRAsset asset)
            => asset;

        public CacheReadResult Read(string cachePath, string originalPath, DateTime sourceTimestampUtc)
        {
            if (!File.Exists(cachePath))
                return CacheReadResult.Miss();

            try
            {
                using FileStream stream = new(
                    cachePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (!ModelBinaryContainerReader.HasMagic(stream))
                {
                    return CacheReadResult.Rejected(
                        CacheRejectReason.LegacyFormat,
                        "The exclusive model cache entry is not a model binary container.");
                }

                ModelBinaryContainerReadResult readResult =
                    ModelBinaryContainerReader.ReadManifest(stream);
                if (!readResult.IsSuccess)
                    return CacheReadResult.Rejected(readResult.Reason, readResult.Detail);

                ModelBinaryCachePreamble preamble = readResult.Container!.Preamble;
                FileInfo sourceInfo = new(originalPath);
                if (!sourceInfo.Exists)
                    return CacheReadResult.Rejected(CacheRejectReason.EntrySourceMissing, "The model source no longer exists.");
                if ((ulong)sourceInfo.Length != preamble.EntrySourceLength)
                    return CacheReadResult.Rejected(CacheRejectReason.SourceLengthMismatch, "The model source length changed.");

                long sourceTicks = sourceTimestampUtc.Kind == DateTimeKind.Utc
                    ? sourceTimestampUtc.Ticks
                    : sourceTimestampUtc.ToUniversalTime().Ticks;
                if (sourceTicks != preamble.EntrySourceLastWriteUtcTicks)
                    return CacheReadResult.Rejected(CacheRejectReason.SourceTimestampMismatch, "The model source timestamp changed.");

                if (preamble.EntrySourceHashMode == ModelBinarySourceHashMode.XxHash3_64
                    && ComputeSourceHash(originalPath) != preamble.EntrySourceHash)
                    return CacheReadResult.Rejected(CacheRejectReason.SourceHashMismatch, "The model source content hash changed.");

                return CacheReadResult.Rejected(
                    CacheRejectReason.CodecUnavailable,
                    "The binary manifest is valid, but live prefab hydration is implemented in a later cache phase.");
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                return CacheReadResult.Rejected(CacheRejectReason.Unreadable, exception.Message);
            }
        }

        public CacheWriteResult Write(string cachePath, XRAsset cacheAsset, XRAsset originalAsset)
            => CacheWriteResult.Skipped(
                CacheRejectReason.CodecUnavailable,
                "The binary container writer is available, but publication awaits cooked model payloads.");

        private static ulong ComputeSourceHash(string path)
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            XxHash3 hash = new();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                    hash.Append(buffer.AsSpan(0, read));
                return hash.GetCurrentHashAsUInt64();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
