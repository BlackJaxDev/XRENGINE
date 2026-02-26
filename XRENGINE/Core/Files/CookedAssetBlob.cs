using System.Diagnostics.CodeAnalysis;
using MemoryPack;
using XREngine.Core;

namespace XREngine.Core.Files
{
    public enum CookedAssetFormat : byte
    {
        BinaryV1 = 1,
    }

    [MemoryPackable]
    public partial struct CookedAssetBlob(string typeName, CookedAssetFormat format, byte[] payload)
    {
        public string TypeName { get; set; } = typeName;

        public CookedAssetFormat Format { get; set; } = format;

        public byte[] Payload { get; set; } = payload;
    }

    public static class CookedAssetReader
    {
        private const string ReflectionWarningMessage = "Cooked cooked asset loading relies on reflection and cannot be statically analyzed for trimming or AOT";

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        public static T? LoadAsset<T>(byte[] cookedData)
        {
            object? value = LoadAsset(cookedData, typeof(T));
            return value is T typed ? typed : default;
        }

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        public static object? LoadAsset(byte[] cookedData, Type? expectedType = null)
        {
            ArgumentNullException.ThrowIfNull(cookedData);

            var blob = MemoryPackSerializer.Deserialize<CookedAssetBlob>(cookedData);
            return blob.Format switch
            {
                CookedAssetFormat.BinaryV1 => DeserializeBinary(blob, expectedType),
                _ => throw new NotSupportedException($"Unsupported cooked asset format '{blob.Format}'.")
            };
        }

        /// <summary>
        /// Loads a cooked asset from a <see cref="ReadOnlySpan{T}"/> without requiring
        /// a <c>byte[]</c> allocation for the outer envelope.  The decompressed bytes
        /// from the archive can be passed directly here.
        /// </summary>
        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        public static object? LoadAsset(ReadOnlySpan<byte> cookedData, Type? expectedType = null)
        {
            if (cookedData.IsEmpty)
                throw new ArgumentException("Cooked data is empty.", nameof(cookedData));

            var blob = MemoryPackSerializer.Deserialize<CookedAssetBlob>(cookedData);
            return blob.Format switch
            {
                CookedAssetFormat.BinaryV1 => DeserializeBinary(blob, expectedType),
                _ => throw new NotSupportedException($"Unsupported cooked asset format '{blob.Format}'.")
            };
        }

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        private static object? DeserializeBinary(CookedAssetBlob blob, Type? expectedType)
        {
            var resolvedType = ResolveType(blob.TypeName) ?? expectedType
                ?? throw new InvalidOperationException($"Unable to resolve cooked asset type '{blob.TypeName}'.");

            return CookedBinarySerializer.Deserialize(resolvedType, blob.Payload);
        }

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        private static Type? ResolveType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            typeName = XRTypeRedirectRegistry.RewriteTypeName(typeName);

            Type? type = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (type is not null)
                return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
                if (type is not null)
                    return type;
            }

            return null;
        }
    }
}
