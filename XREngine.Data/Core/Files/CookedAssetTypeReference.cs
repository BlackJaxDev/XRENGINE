using System.Diagnostics.CodeAnalysis;
using XREngine.Data.Runtime.AotParity;

namespace XREngine.Core.Files
{
    public static class CookedAssetTypeReference
    {
        private const string AotTypeIndexPrefix = "aot:";
        private const string ContractPrefix = "contract:";

        public static string Encode(Type runtimeType, AotRuntimeMetadata? metadata = null)
        {
            ArgumentNullException.ThrowIfNull(runtimeType);

            if (RuntimeTypeContractRegistry.TryGet(runtimeType, out string? contractId, out int schemaVersion))
                return $"{ContractPrefix}{contractId}:{schemaVersion}";

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

        /// <summary>
        /// Resolves a type reference using only the paths available to a published runtime:
        /// the encoded metadata index, the metadata known-type table, and published asset
        /// registrations. It never scans assemblies, so it carries no reflection annotations.
        /// </summary>
        public static bool TryResolvePublished(string? typeReference, Type? expectedType, out Type? resolved)
        {
            if (TryResolveEncodedTypeReference(typeReference, out resolved))
            {
                resolved ??= expectedType;
                return resolved is not null;
            }

            if (!string.IsNullOrWhiteSpace(typeReference))
            {
                string rewritten = XRTypeRedirectRegistry.RewriteTypeName(typeReference);
                resolved = AotRuntimeMetadataStore.ResolveType(rewritten);
                if (resolved is not null)
                    return true;
            }

            resolved = expectedType;
            return resolved is not null;
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
            string rewrittenFullName = SerializedTypeIdentity.GetUnqualifiedTypeName(rewritten);

            return string.Equals(rewrittenFullName, expectedType.FullName, StringComparison.Ordinal)
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

            if (XRRuntimeEnvironment.IsPublishedBuild)
                return null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                resolved = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
                if (resolved is not null)
                {
                    AotParityDiagnostics.Report(
                        resolved,
                        EAotParityCategory.TypeResolutionScan,
                        $"{nameof(CookedAssetTypeReference)}.{nameof(Resolve)}",
                        $"Add the type to the published runtime metadata known-type table or register it with {nameof(PublishedCookedAssetRegistry)} so the cooked type reference resolves without scanning assemblies.");
                    return resolved;
                }
            }

            return null;
        }

        private static bool TryResolveEncodedTypeReference(string? typeReference, out Type? resolved)
        {
            resolved = null;

            if (typeReference?.StartsWith(ContractPrefix, StringComparison.Ordinal) == true)
            {
                ReadOnlySpan<char> identity = typeReference.AsSpan(ContractPrefix.Length);
                int separator = identity.LastIndexOf(':');
                if (separator <= 0 || !int.TryParse(identity[(separator + 1)..], out int schemaVersion) ||
                    !RuntimeTypeContractRegistry.TryResolve(identity[..separator].ToString(), out Type? registered) ||
                    registered is null ||
                    !RuntimeTypeContractRegistry.TryGet(registered, out _, out int installedVersion) ||
                    schemaVersion != installedVersion)
                {
                    throw new InvalidOperationException($"Cooked contract '{typeReference}' is unavailable or has a different schema version. Re-cook the asset against the current runtime contracts.");
                }
                resolved = registered;
                return true;
            }

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
