using System.Diagnostics.CodeAnalysis;

namespace XREngine.Core.Files
{
    public static class CookedAssetTypeReference
    {
        private const string AotTypeIndexPrefix = "aot:";

        public static string Encode(Type runtimeType, AotRuntimeMetadata? metadata = null)
        {
            ArgumentNullException.ThrowIfNull(runtimeType);

            if (TryGetKnownTypeIndex(runtimeType, metadata, out int typeIndex))
                return $"{AotTypeIndexPrefix}{typeIndex}";

            return runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name;
        }

        [RequiresUnreferencedCode(CookedAssetReader.ReflectionWarningMessage)]
        [RequiresDynamicCode(CookedAssetReader.ReflectionWarningMessage)]
        public static Type? Resolve(string? typeReference, Type? expectedType = null)
        {
            if (TryResolveEncodedTypeReference(typeReference, out Type? resolved))
                return resolved ?? expectedType;

            resolved = ResolveByName(typeReference);
            if (resolved is not null)
                return resolved;

            return expectedType;
        }

        public static bool MatchesExpectedType(string? typeReference, Type expectedType)
        {
            ArgumentNullException.ThrowIfNull(expectedType);

            if (TryResolveEncodedTypeReference(typeReference, out Type? resolved))
                return resolved is null || expectedType.IsAssignableFrom(resolved);

            if (string.IsNullOrWhiteSpace(typeReference))
                return true;

            string rewritten = XRTypeRedirectRegistry.RewriteTypeName(typeReference);
            string? assemblyQualifiedName = expectedType.AssemblyQualifiedName;

            return string.Equals(rewritten, expectedType.FullName, StringComparison.Ordinal)
                || string.Equals(rewritten, expectedType.Name, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(assemblyQualifiedName) && string.Equals(rewritten, assemblyQualifiedName, StringComparison.Ordinal));
        }

        private static bool TryGetKnownTypeIndex(Type runtimeType, AotRuntimeMetadata? metadata, out int typeIndex)
        {
            typeIndex = -1;

            string? assemblyQualifiedName = runtimeType.AssemblyQualifiedName;
            if (string.IsNullOrWhiteSpace(assemblyQualifiedName))
                return false;

            if (metadata is not null)
            {
                string[] knownTypes = metadata.KnownTypeAssemblyQualifiedNames;
                for (int i = 0; i < knownTypes.Length; i++)
                {
                    if (string.Equals(knownTypes[i], assemblyQualifiedName, StringComparison.Ordinal))
                    {
                        typeIndex = i;
                        return true;
                    }
                }

                return false;
            }

            return AotRuntimeMetadataStore.TryGetKnownTypeIndex(runtimeType, out typeIndex);
        }

        [RequiresUnreferencedCode(CookedAssetReader.ReflectionWarningMessage)]
        [RequiresDynamicCode(CookedAssetReader.ReflectionWarningMessage)]
        private static Type? ResolveByName(string? typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            typeName = XRTypeRedirectRegistry.RewriteTypeName(typeName);

            Type? resolved = AotRuntimeMetadataStore.ResolveType(typeName);
            if (resolved is not null)
                return resolved;

            if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                return null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                resolved = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
                if (resolved is not null)
                    return resolved;
            }

            return null;
        }

        private static bool TryResolveEncodedTypeReference(string? typeReference, out Type? resolved)
        {
            resolved = null;

            if (string.IsNullOrWhiteSpace(typeReference)
                || !typeReference.StartsWith(AotTypeIndexPrefix, StringComparison.Ordinal)
                || !int.TryParse(typeReference.AsSpan(AotTypeIndexPrefix.Length), out int typeIndex))
            {
                return false;
            }

            resolved = AotRuntimeMetadataStore.ResolveType(typeIndex);
            return true;
        }
    }
}