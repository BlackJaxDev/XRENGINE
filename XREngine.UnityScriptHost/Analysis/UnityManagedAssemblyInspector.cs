using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace XREngine.UnityScriptHost.Analysis;

/// <summary>Reads bounded PE metadata without loading the inspected assembly.</summary>
public static class UnityManagedAssemblyInspector
{
    private const int MaxAssemblyBytes = 64 * 1024 * 1024;
    private const int MaxReferences = 4096;
    private const int MaxMethods = 200_000;
    private const int MaxTextLength = 2048;
    private const int MaxTotalTextLength = 1024 * 1024;
    private const int MaxPublicKeyBytes = 16 * 1024;

    /// <summary>Reads identity and native entry-point requirements from a managed DLL.</summary>
    public static UnityManagedAssemblyInfo Inspect(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > MaxAssemblyBytes)
            throw new InvalidDataException("A managed DLL must fit the inspection byte limit.");

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            if ((pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) == 0)
                throw new InvalidDataException("The imported managed assembly must be a DLL.");
            CorHeader? corHeader = pe.PEHeaders.CorHeader;
            if (corHeader is null || !pe.HasMetadata)
                throw new InvalidDataException("The imported DLL has no managed assembly metadata.");
            if ((corHeader.Flags & CorFlags.ILOnly) == 0)
                throw new InvalidDataException("Mixed-mode managed DLLs are not supported.");

            MetadataReader metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
                throw new InvalidDataException("A managed netmodule is not an assembly.");
            int moduleReferenceCount = metadata.GetTableRowCount(TableIndex.ModuleRef);
            if (metadata.AssemblyReferences.Count > MaxReferences ||
                moduleReferenceCount > MaxReferences ||
                metadata.MethodDefinitions.Count > MaxMethods)
                throw new InvalidDataException("Managed DLL metadata exceeds the inspection item limit.");

            int decodedTextLength = 0;
            AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
            string name = ReadName(metadata, assembly.Name, ref decodedTextLength);
            string culture = ReadCulture(metadata, assembly.Culture, ref decodedTextLength);
            string token = PublicKeyToken(metadata, assembly.PublicKey, isFullKey: true);
            var references = new List<UnityManagedAssemblyReference>(metadata.AssemblyReferences.Count);
            foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
            {
                AssemblyReference reference = metadata.GetAssemblyReference(handle);
                references.Add(new UnityManagedAssemblyReference
                {
                    Name = ReadName(metadata, reference.Name, ref decodedTextLength),
                    Version = reference.Version.ToString(),
                    Culture = ReadCulture(metadata, reference.Culture, ref decodedTextLength),
                    PublicKeyToken = PublicKeyToken(metadata, reference.PublicKeyOrToken,
                        (reference.Flags & AssemblyFlags.PublicKey) != 0),
                });
            }

            var moduleReferences = new List<string>(moduleReferenceCount);
            for (int row = 1; row <= moduleReferenceCount; row++)
            {
                ModuleReferenceHandle handle = MetadataTokens.ModuleReferenceHandle(row);
                moduleReferences.Add(ReadName(metadata, metadata.GetModuleReference(handle).Name,
                    ref decodedTextLength));
            }

            int pInvokeCount = 0;
            int internalCallCount = 0;
            int nativeBodyCount = 0;
            foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
            {
                MethodDefinition method = metadata.GetMethodDefinition(handle);
                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                    pInvokeCount++;
                if ((method.ImplAttributes & MethodImplAttributes.InternalCall) != 0)
                    internalCallCount++;
                if ((method.ImplAttributes & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.Native)
                    nativeBodyCount++;
            }

            return new UnityManagedAssemblyInfo
            {
                Name = name,
                Version = assembly.Version.ToString(),
                Culture = culture,
                PublicKeyToken = token,
                IsIlOnly = true,
                Requires32Bit = (corHeader.Flags & CorFlags.Requires32Bit) != 0,
                References = [.. references],
                ModuleReferences = [.. moduleReferences],
                PInvokeMethodCount = pInvokeCount,
                InternalCallMethodCount = internalCallCount,
                NativeMethodBodyCount = nativeBodyCount,
                TypeDefinitionCount = metadata.TypeDefinitions.Count,
            };
        }
        catch (BadImageFormatException exception)
        {
            throw new InvalidDataException("The imported DLL has invalid PE metadata.", exception);
        }
    }

    private static string ReadName(MetadataReader metadata, StringHandle handle, ref int decodedTextLength)
    {
        string value = metadata.GetString(handle);
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxTextLength)
            throw new InvalidDataException("A managed assembly or module name is invalid or too long.");
        AddDecodedText(value, ref decodedTextLength);
        return value;
    }

    private static string ReadCulture(MetadataReader metadata, StringHandle handle, ref int decodedTextLength)
    {
        string value = handle.IsNil ? string.Empty : metadata.GetString(handle);
        if (value.Length > MaxTextLength)
            throw new InvalidDataException("A managed assembly culture is too long.");
        AddDecodedText(value, ref decodedTextLength);
        return value;
    }

    private static void AddDecodedText(string value, ref int decodedTextLength)
    {
        if (value.Length > MaxTotalTextLength - decodedTextLength)
            throw new InvalidDataException("Managed DLL identity text exceeds the inspection limit.");
        decodedTextLength += value.Length;
    }

    private static string PublicKeyToken(MetadataReader metadata, BlobHandle handle, bool isFullKey)
    {
        if (handle.IsNil)
            return string.Empty;
        int length = metadata.GetBlobReader(handle).Length;
        if (isFullKey && length > MaxPublicKeyBytes)
            throw new InvalidDataException("A managed assembly public key exceeds the inspection limit.");
        if (!isFullKey && length is not 0 and not 8)
            throw new InvalidDataException("An assembly reference has an invalid public key token.");
        byte[] key = metadata.GetBlobBytes(handle);
        if (key.Length == 0)
            return string.Empty;
        if (!isFullKey)
            return Convert.ToHexStringLower(key);

        byte[] digest = SHA1.HashData(key);
        Span<byte> token = stackalloc byte[8];
        for (int index = 0; index < token.Length; index++)
            token[index] = digest[digest.Length - 1 - index];
        return Convert.ToHexStringLower(token);
    }
}
