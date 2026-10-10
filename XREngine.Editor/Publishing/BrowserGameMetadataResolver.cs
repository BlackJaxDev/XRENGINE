using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace XREngine.Editor.Publishing;

/// <summary>Inspects adjacent and framework declarations without loading authored code.</summary>
internal sealed class BrowserGameMetadataResolver : IDisposable
{
    private readonly string _directory;
    private readonly MetadataReader _source;
    private readonly Dictionary<string, AssemblyReference> _expected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedAssembly?> _assemblies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _frameworkPaths = new(StringComparer.Ordinal);

    internal BrowserGameMetadataResolver(string assemblyPath, MetadataReader source)
    {
        _directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        _source = source;
        foreach (AssemblyReferenceHandle handle in source.AssemblyReferences)
        {
            AssemblyReference reference = source.GetAssemblyReference(handle);
            string name = source.GetString(reference.Name);
            if (!_expected.TryAdd(name, reference))
                throw new InvalidDataException($"BrowserGameAudit.DuplicateAssemblyIdentity: multiple references to '{name}' cannot be resolved by simple file name.");
        }
        // Runtime implementations can carry host-specific annotations (for example,
        // System.Console on Unix). Inspect the reference assemblies used to compile games.
        DirectoryInfo runtime = new(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        string? referenceDirectory = null;
        foreach (string? root in new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                     runtime.Parent?.Parent?.Parent?.FullName }.Distinct(StringComparer.Ordinal))
        {
            if (root is null)
                continue;
            string packs = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(packs))
                continue;
            string framework = $"net{Environment.Version.Major}.0";
            string exact = Path.Combine(packs, runtime.Name, "ref", framework);
            if (Directory.Exists(exact))
            {
                referenceDirectory = exact;
                break;
            }
            Version? bestVersion = null;
            foreach (string candidate in Directory.EnumerateDirectories(packs))
            {
                if (!Version.TryParse(Path.GetFileName(candidate), out Version? version) ||
                    version.Major != Environment.Version.Major ||
                    !Directory.Exists(Path.Combine(candidate, "ref", framework)) ||
                    bestVersion is not null && version <= bestVersion)
                    continue;
                bestVersion = version;
                referenceDirectory = Path.Combine(candidate, "ref", framework);
            }
            if (referenceDirectory is not null)
                break;
        }
        if (referenceDirectory is null)
            throw new DirectoryNotFoundException("BrowserGameAudit.ReferencePackMissing: the active .NET reference pack is required to inspect framework browser annotations.");
        foreach (string path in Directory.EnumerateFiles(referenceDirectory, "*.dll"))
            _frameworkPaths.TryAdd(Path.GetFileNameWithoutExtension(path), path);
    }

    internal string? GetAssemblyReason(string assembly)
    {
        ResolvedAssembly? found = Open(assembly);
        return found is null ? null : GetPlatformReason(found.Metadata, found.Metadata.GetAssemblyDefinition().GetCustomAttributes());
    }

    internal string? GetTypeReason(BrowserGameTypeName type)
    {
        ResolvedType? found = Resolve(type);
        if (found is null)
            return null;
        TypeDefinitionHandle handle = found.Value.Handle;
        while (!handle.IsNil)
        {
            TypeDefinition definition = found.Value.Metadata.GetTypeDefinition(handle);
            if (GetPlatformReason(found.Value.Metadata, definition.GetCustomAttributes()) is string reason)
                return reason;
            handle = definition.GetDeclaringType();
        }
        return null;
    }

    internal string? GetMemberReason(MetadataReader source, MemberReference member, BrowserGameTypeName owner)
    {
        ResolvedType? found = Resolve(owner);
        if (found is null)
            return null;
        MetadataReader target = found.Value.Metadata;
        TypeDefinition type = target.GetTypeDefinition(found.Value.Handle);
        string name = source.GetString(member.Name);
        BrowserGameTypeNames sourceNames = new(source, owner.Assembly);
        BrowserGameTypeNames targetNames = new(target, target.GetString(target.GetAssemblyDefinition().Name));
        List<string?> matches = [];

        if (member.GetKind() == MemberReferenceKind.Method)
        {
            MethodSignature<BrowserGameTypeName> signature;
            try { signature = member.DecodeMethodSignature(sourceNames, null); }
            catch (BadImageFormatException) { return null; }
            foreach (MethodDefinitionHandle handle in type.GetMethods())
            {
                MethodDefinition candidate = target.GetMethodDefinition(handle);
                if (target.GetString(candidate.Name) != name)
                    continue;
                MethodSignature<BrowserGameTypeName> declared;
                try { declared = candidate.DecodeSignature(targetNames, null); }
                catch (BadImageFormatException) { continue; }
                if (!SameSignature(signature, declared))
                    continue;
                matches.Add(GetPlatformReason(target, candidate.GetCustomAttributes())
                    ?? GetAccessorReason(target, type, handle));
            }
        }
        else if (member.GetKind() == MemberReferenceKind.Field)
        {
            BrowserGameTypeName signature;
            try { signature = member.DecodeFieldSignature(sourceNames, null); }
            catch (BadImageFormatException) { return null; }
            foreach (FieldDefinitionHandle handle in type.GetFields())
            {
                FieldDefinition candidate = target.GetFieldDefinition(handle);
                if (target.GetString(candidate.Name) != name)
                    continue;
                BrowserGameTypeName declared;
                try { declared = candidate.DecodeSignature(targetNames, null); }
                catch (BadImageFormatException) { continue; }
                if (signature.SignatureKey == declared.SignatureKey)
                    matches.Add(GetPlatformReason(target, candidate.GetCustomAttributes()));
            }
        }

        // An ambiguous match must not reject a portable overload.
        return matches.Count != 0 && matches.All(reason => reason is not null) ? matches[0] : null;
    }

    private static bool SameSignature(MethodSignature<BrowserGameTypeName> left, MethodSignature<BrowserGameTypeName> right)
    {
        if (left.GenericParameterCount != right.GenericParameterCount ||
            left.ParameterTypes.Length != right.ParameterTypes.Length ||
            left.ReturnType.SignatureKey != right.ReturnType.SignatureKey)
            return false;
        for (int i = 0; i < left.ParameterTypes.Length; i++)
            if (left.ParameterTypes[i].SignatureKey != right.ParameterTypes[i].SignatureKey)
                return false;
        return true;
    }

    private static string? GetAccessorReason(MetadataReader metadata, TypeDefinition type, MethodDefinitionHandle accessor)
    {
        foreach (PropertyDefinitionHandle handle in type.GetProperties())
        {
            PropertyDefinition property = metadata.GetPropertyDefinition(handle);
            PropertyAccessors accessors = property.GetAccessors();
            if (accessors.Getter == accessor || accessors.Setter == accessor)
                return GetPlatformReason(metadata, property.GetCustomAttributes());
        }
        foreach (EventDefinitionHandle handle in type.GetEvents())
        {
            EventDefinition eventDefinition = metadata.GetEventDefinition(handle);
            EventAccessors accessors = eventDefinition.GetAccessors();
            if (accessors.Adder == accessor || accessors.Remover == accessor || accessors.Raiser == accessor)
                return GetPlatformReason(metadata, eventDefinition.GetCustomAttributes());
        }
        return null;
    }

    private ResolvedType? Resolve(BrowserGameTypeName identity, int depth = 0)
    {
        if (depth == 8)
            return null;
        ResolvedAssembly? assembly = Open(identity.Assembly);
        if (assembly is null)
            return null;
        if (assembly.Types.TryGetValue(identity.FullName, out TypeDefinitionHandle handle))
            return new(assembly.Metadata, handle);
        if (!assembly.Forwarders.TryGetValue(identity.FullName, out ExportedTypeHandle forwarded))
            return null;
        EntityHandle implementation = assembly.Metadata.GetExportedType(forwarded).Implementation;
        int enclosingDepth = 0;
        while (implementation.Kind == HandleKind.ExportedType)
        {
            if (++enclosingDepth == 64)
                throw new InvalidDataException("BrowserGameAudit.InvalidMetadata: exported type nesting exceeds the audit limit.");
            implementation = assembly.Metadata.GetExportedType((ExportedTypeHandle)implementation).Implementation;
        }
        if (implementation.Kind != HandleKind.AssemblyReference)
            return null;
        AssemblyReference reference = assembly.Metadata.GetAssemblyReference((AssemblyReferenceHandle)implementation);
        string targetName = assembly.Metadata.GetString(reference.Name);
        ResolvedAssembly? target = Open(targetName);
        if (target is null)
            return null;
        if (!MatchesReference(assembly.Metadata, reference, target.Metadata))
            throw new InvalidDataException($"BrowserGameAudit.AssemblyIdentityMismatch: forwarded assembly '{targetName}' does not match its referenced identity.");
        return Resolve(new(targetName, identity.FullName), depth + 1);
    }

    private ResolvedAssembly? Open(string name)
    {
        if (name.IndexOfAny(['/', '\\']) >= 0)
            throw new InvalidDataException("BrowserGameAudit.InvalidMetadata: an assembly reference is not a simple file name.");
        if (_assemblies.TryGetValue(name, out ResolvedAssembly? cached))
            return cached;
        string local = Path.Combine(_directory, name + ".dll");
        string? path = File.Exists(local) ? local : _frameworkPaths.GetValueOrDefault(name);
        if (path is null || !File.Exists(path))
        {
            _assemblies.Add(name, null);
            return null;
        }
        FileStream stream = File.OpenRead(path);
        PEReader? image = null;
        try
        {
            image = new PEReader(stream);
            if (!image.HasMetadata)
            {
                image.Dispose();
                _assemblies.Add(name, null);
                return null;
            }
            MetadataReader metadata = image.GetMetadataReader();
            if (metadata.GetString(metadata.GetAssemblyDefinition().Name) != name ||
                _expected.TryGetValue(name, out AssemblyReference expected) &&
                !MatchesReference(_source, expected, metadata))
                throw new InvalidDataException($"BrowserGameAudit.AssemblyIdentityMismatch: resolved assembly '{name}' does not match its referenced identity.");
            Dictionary<string, TypeDefinitionHandle> types = new(StringComparer.Ordinal);
            Dictionary<string, ExportedTypeHandle> forwarders = new(StringComparer.Ordinal);
            BrowserGameTypeNames names = new(metadata, name);
            foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
                types.TryAdd(names.FromDefinition(handle).FullName, handle);
            foreach (ExportedTypeHandle handle in metadata.ExportedTypes)
                forwarders.TryAdd(GetExportedName(metadata, handle), handle);
            ResolvedAssembly result = new(image, metadata, types, forwarders);
            _assemblies.Add(name, result);
            return result;
        }
        catch
        {
            if (image is null) stream.Dispose();
            else image.Dispose();
            throw;
        }
    }

    private static string GetExportedName(MetadataReader metadata, ExportedTypeHandle handle, int depth = 0)
    {
        if (depth == 64)
            throw new InvalidDataException("BrowserGameAudit.InvalidMetadata: exported type nesting exceeds the audit limit.");
        ExportedType type = metadata.GetExportedType(handle);
        string name = metadata.GetString(type.Name);
        if (type.Implementation.Kind == HandleKind.ExportedType)
            return GetExportedName(metadata, (ExportedTypeHandle)type.Implementation, depth + 1) + "+" + name;
        string ns = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static bool MatchesReference(MetadataReader source, AssemblyReference expected, MetadataReader target)
    {
        AssemblyDefinition actual = target.GetAssemblyDefinition();
        if (expected.Version != actual.Version ||
            !string.Equals(source.GetString(expected.Culture), target.GetString(actual.Culture), StringComparison.OrdinalIgnoreCase))
            return false;
        byte[] requested = source.GetBlobBytes(expected.PublicKeyOrToken);
        byte[] publicKey = target.GetBlobBytes(actual.PublicKey);
        if ((expected.Flags & System.Reflection.AssemblyFlags.PublicKey) != 0)
            return requested.AsSpan().SequenceEqual(publicKey);
        if (requested.Length == 0)
            return publicKey.Length == 0;
        if (requested.Length != 8 || publicKey.Length == 0)
            return false;
        byte[] hash = SHA1.HashData(publicKey);
        for (int index = 0; index < requested.Length; index++)
            if (requested[index] != hash[hash.Length - 1 - index])
                return false;
        return true;
    }

    internal static string? GetPlatformReason(MetadataReader metadata, CustomAttributeHandleCollection attributes)
    {
        bool supportedOnBrowser = false;
        string? restrictedTo = null;
        foreach (CustomAttributeHandle handle in attributes)
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
                continue;
            MemberReference constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference)
                continue;
            TypeReference owner = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (metadata.GetString(owner.Namespace) != "System.Runtime.Versioning")
                continue;
            string kind = metadata.GetString(owner.Name);
            if (kind is not ("SupportedOSPlatformAttribute" or "UnsupportedOSPlatformAttribute"))
                continue;
            BlobReader value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1)
                continue;
            string? platform = value.ReadSerializedString();
            if (platform is null)
                continue;
            if (kind == "UnsupportedOSPlatformAttribute" &&
                platform.StartsWith("browser", StringComparison.OrdinalIgnoreCase))
                return "unsupported on browser";
            if (kind == "SupportedOSPlatformAttribute")
            {
                if (platform.Equals("browser", StringComparison.OrdinalIgnoreCase))
                    supportedOnBrowser = true;
                else
                    restrictedTo ??= platform;
            }
        }
        return supportedOnBrowser || restrictedTo is null ? null : $"supported only on {restrictedTo}";
    }

    public void Dispose()
    {
        foreach (ResolvedAssembly? assembly in _assemblies.Values)
            assembly?.Image.Dispose();
    }

    private readonly record struct ResolvedType(MetadataReader Metadata, TypeDefinitionHandle Handle);
    private sealed record ResolvedAssembly(PEReader Image, MetadataReader Metadata,
        Dictionary<string, TypeDefinitionHandle> Types, Dictionary<string, ExportedTypeHandle> Forwarders);
}
