using System.Text;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Fixed constants and explicit IDs for model binary cache schema v1.
/// </summary>
internal static class ModelBinaryCacheFormat
{
    public static ReadOnlySpan<byte> Magic => "XRE_MODEL_CACHE\0"u8;

    public const int PreambleSize = 308;
    public const int ChunkEntrySize = 64;
    public const int Alignment = 16;

    public const int HeaderChecksumOffset = 40;
    public const int StringPoolOffsetOffset = 48;
    public const int StringPoolLengthOffset = 56;
    public const int ChunkTableOffsetOffset = 64;
    public const int ChunkTableLengthOffset = 72;
    public const int ChunkTableChecksumOffset = 80;
    public const int StringPoolChecksumOffset = 88;
    public const int ChunkCountOffset = 96;
    public const int DependencyManifestHashOffset = 240;
    public const int DependencyCountOffset = 256;
    public const int SourceIdentityOffset = 264;
    public const int EngineBuildIdentityOffset = 268;
    public const int ReservedOffset = 276;

    public const int DependencyHeaderSize = 16;
    public const int DependencyRecordSize = 40;
    public const int ManifestHeaderSize = 144;
    public const int ManifestCandidateRecordSize = 16;

    public const uint DependencyRequiredFlag = 1;
    public const uint ManifestFormatVersion = 1;

    private static readonly UTF8Encoding StrictUtf8Instance = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static Encoding StrictUtf8 => StrictUtf8Instance;

    public static int Align(int value)
    {
        int remainder = value % Alignment;
        return remainder == 0 ? value : checked(value + Alignment - remainder);
    }

    public static ulong Align(ulong value)
    {
        ulong remainder = value % Alignment;
        return remainder == 0 ? value : checked(value + Alignment - remainder);
    }

    public static bool IsKnownChunkType(uint typeId)
        => typeId is >= (uint)ModelBinaryChunkType.Dependencies
            and <= (uint)ModelBinaryChunkType.Diagnostics;

    public static uint GetChunkVersion(uint typeId)
        => (ModelBinaryChunkType)typeId switch
        {
            ModelBinaryChunkType.Dependencies => ModelBinaryChunkVersions.Dependencies,
            ModelBinaryChunkType.Manifest => ModelBinaryChunkVersions.Manifest,
            ModelBinaryChunkType.PrefabGraph => ModelBinaryChunkVersions.PrefabGraph,
            ModelBinaryChunkType.ComponentDirectory => ModelBinaryChunkVersions.ComponentDirectory,
            ModelBinaryChunkType.ComponentPayloads => ModelBinaryChunkVersions.ComponentPayloads,
            ModelBinaryChunkType.Models => ModelBinaryChunkVersions.Models,
            ModelBinaryChunkType.SubMeshes => ModelBinaryChunkVersions.SubMeshes,
            ModelBinaryChunkType.MeshDirectory => ModelBinaryChunkVersions.MeshDirectory,
            ModelBinaryChunkType.MeshCoreStreams => ModelBinaryChunkVersions.MeshCoreStreams,
            ModelBinaryChunkType.Skinning => ModelBinaryChunkVersions.Skinning,
            ModelBinaryChunkType.Skeletons => ModelBinaryChunkVersions.Skeletons,
            ModelBinaryChunkType.MorphTargets => ModelBinaryChunkVersions.MorphTargets,
            ModelBinaryChunkType.LodTables => ModelBinaryChunkVersions.LodTables,
            ModelBinaryChunkType.Meshlets => ModelBinaryChunkVersions.Meshlets,
            ModelBinaryChunkType.Materials => ModelBinaryChunkVersions.Materials,
            ModelBinaryChunkType.TextureReferences => ModelBinaryChunkVersions.TextureReferences,
            ModelBinaryChunkType.AnimationReferences => ModelBinaryChunkVersions.AnimationReferences,
            ModelBinaryChunkType.ImportedEntityTable => ModelBinaryChunkVersions.ImportedEntityTable,
            ModelBinaryChunkType.ColliderHints => ModelBinaryChunkVersions.ColliderHints,
            ModelBinaryChunkType.Diagnostics => ModelBinaryChunkVersions.Diagnostics,
            _ => 0,
        };

    public static bool IsSingletonChunk(uint typeId)
        => (ModelBinaryChunkType)typeId is
            ModelBinaryChunkType.Dependencies
            or ModelBinaryChunkType.Manifest
            or ModelBinaryChunkType.PrefabGraph
            or ModelBinaryChunkType.ComponentDirectory
            or ModelBinaryChunkType.Models
            or ModelBinaryChunkType.SubMeshes
            or ModelBinaryChunkType.MeshDirectory
            or ModelBinaryChunkType.Materials
            or ModelBinaryChunkType.TextureReferences
            or ModelBinaryChunkType.AnimationReferences
            or ModelBinaryChunkType.ImportedEntityTable
            or ModelBinaryChunkType.ColliderHints
            or ModelBinaryChunkType.Diagnostics;

    public static bool AllowsEmptyChunk(uint typeId)
        => (ModelBinaryChunkType)typeId is
            ModelBinaryChunkType.ColliderHints
            or ModelBinaryChunkType.Diagnostics;
}
