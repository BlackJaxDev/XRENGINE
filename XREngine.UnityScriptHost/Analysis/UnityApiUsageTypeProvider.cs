using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using System.Text;

namespace XREngine.UnityScriptHost.Analysis;

/// <summary>Decodes metadata signatures into bounded names without loading referenced assemblies.</summary>
internal sealed class UnityApiUsageTypeProvider(MetadataReader metadata, string currentAssembly)
    : ISignatureTypeProvider<UnityApiUsageTypeName, object?>
{
    private const int MaxDepth = 32;
    private const int MaxTextLength = 4096;
    private const int MaxEncodedSignatureBytes = 256;
    private int _specificationDepth;
    private int _remainingSignatureBytes;

    internal void BeginMember() => _remainingSignatureBytes = MaxEncodedSignatureBytes;

    internal void ChargeSignature(BlobHandle handle)
    {
        int length = metadata.GetBlobReader(handle).Length;
        if (length < 1 || length > _remainingSignatureBytes)
            throw new InvalidDataException("Encoded member signatures exceed the metadata byte limit.");
        _remainingSignatureBytes -= length;
    }

    internal UnityApiUsageTypeName FromParent(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => FromReference((TypeReferenceHandle)parent, 0),
        HandleKind.TypeDefinition => FromDefinition((TypeDefinitionHandle)parent, 0),
        HandleKind.TypeSpecification => GetTypeFromSpecification(metadata, null, (TypeSpecificationHandle)parent, 0),
        HandleKind.ModuleReference => UnresolvedModule((ModuleReferenceHandle)parent),
        HandleKind.MethodDefinition => new(currentAssembly, "<method-parent>", IsLocal: true),
        _ => throw new InvalidDataException("A member reference has an unsupported parent kind.")
    };

    private UnityApiUsageTypeName FromReference(TypeReferenceHandle handle, int depth)
    {
        if (depth >= MaxDepth)
            throw new InvalidDataException("Nested type references exceed the metadata depth limit.");
        TypeReference reference = metadata.GetTypeReference(handle);
        string name = EncodeToken(metadata.GetString(reference.Name));
        string ns = EncodeToken(metadata.GetString(reference.Namespace));
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            UnityApiUsageTypeName outer = FromReference((TypeReferenceHandle)reference.ResolutionScope, depth + 1);
            return new(outer.Assembly, Bound(outer.Name + "+" + name), outer.Resolved, outer.IsLocal);
        }

        if (reference.ResolutionScope.Kind == HandleKind.AssemblyReference)
        {
            string assembly = EncodeToken(metadata.GetString(metadata.GetAssemblyReference(
                (AssemblyReferenceHandle)reference.ResolutionScope).Name));
            // A separate AssemblyRef can share this assembly's simple name.
            return new(assembly, Bound(string.IsNullOrEmpty(ns) ? name : ns + "." + name),
                Resolved: assembly != currentAssembly);
        }

        if (reference.ResolutionScope.Kind == HandleKind.ModuleDefinition)
            return new(currentAssembly, Bound(string.IsNullOrEmpty(ns) ? name : ns + "." + name), IsLocal: true);
        if (reference.ResolutionScope.Kind == HandleKind.ModuleReference)
        {
            string module = EncodeToken(metadata.GetString(metadata.GetModuleReference(
                (ModuleReferenceHandle)reference.ResolutionScope).Name));
            return new("<module:" + module + ">", Bound(string.IsNullOrEmpty(ns) ? name : ns + "." + name), false);
        }

        return new("<unresolved>", Bound(string.IsNullOrEmpty(ns) ? name : ns + "." + name), false);
    }

    private UnityApiUsageTypeName FromDefinition(TypeDefinitionHandle handle, int depth)
    {
        if (depth >= MaxDepth)
            throw new InvalidDataException("Nested type definitions exceed the metadata depth limit.");
        TypeDefinition definition = metadata.GetTypeDefinition(handle);
        string name = EncodeToken(metadata.GetString(definition.Name));
        TypeDefinitionHandle outer = definition.GetDeclaringType();
        if (!outer.IsNil)
            name = Bound(FromDefinition(outer, depth + 1).Name + "+" + name);
        else
        {
            string ns = EncodeToken(metadata.GetString(definition.Namespace));
            if (!string.IsNullOrEmpty(ns))
                name = Bound(ns + "." + name);
        }
        return new(currentAssembly, name, IsLocal: true);
    }

    private UnityApiUsageTypeName UnresolvedModule(ModuleReferenceHandle handle)
        => new("<module:" + EncodeToken(metadata.GetString(metadata.GetModuleReference(handle).Name)) + ">",
            "<module>", false);

    public UnityApiUsageTypeName GetArrayType(UnityApiUsageTypeName elementType, ArrayShape shape)
    {
        if (shape.Rank < 1 || shape.Rank > MaxDepth)
            throw new InvalidDataException("Array rank exceeds the metadata limit.");
        string sizes = string.Join(",", shape.Sizes.Select(static size => size.ToString(CultureInfo.InvariantCulture)));
        string lowerBounds = string.Join(",", shape.LowerBounds.Select(
            static lowerBound => lowerBound.ToString(CultureInfo.InvariantCulture)));
        string suffix = string.Create(CultureInfo.InvariantCulture,
            $"[rank={shape.Rank};sizes={sizes};lowerbounds={lowerBounds}]");
        return elementType with { Name = Bound(elementType.Name + suffix) };
    }

    public UnityApiUsageTypeName GetByReferenceType(UnityApiUsageTypeName elementType)
        => elementType with { Name = Bound(elementType.Name + "&") };

    public UnityApiUsageTypeName GetFunctionPointerType(MethodSignature<UnityApiUsageTypeName> signature)
        => new("<signature>", Bound(string.Create(CultureInfo.InvariantCulture,
            $"fnptr#{signature.Header.RawValue:x2}/{signature.GenericParameterCount}/{signature.RequiredParameterCount}({string.Join(",", signature.ParameterTypes.Select(static type => type.QualifiedName))})->{signature.ReturnType.QualifiedName}")),
            signature.ReturnType.Resolved && signature.ParameterTypes.All(static type => type.Resolved));

    public UnityApiUsageTypeName GetGenericInstantiation(UnityApiUsageTypeName genericType,
        ImmutableArray<UnityApiUsageTypeName> typeArguments)
        => genericType with
        {
            Name = Bound($"{genericType.Name}<{string.Join(",", typeArguments.Select(static type => type.QualifiedName))}>"),
            Resolved = genericType.Resolved && typeArguments.All(static type => type.Resolved)
        };

    public UnityApiUsageTypeName GetGenericMethodParameter(object? genericContext, int index)
        => new("<generic>", "!!" + index.ToString(CultureInfo.InvariantCulture));

    public UnityApiUsageTypeName GetGenericTypeParameter(object? genericContext, int index)
        => new("<generic>", "!" + index.ToString(CultureInfo.InvariantCulture));

    public UnityApiUsageTypeName GetModifiedType(UnityApiUsageTypeName modifier,
        UnityApiUsageTypeName unmodifiedType, bool isRequired)
        => unmodifiedType with
        {
            Name = Bound($"{unmodifiedType.Name} {(isRequired ? "modreq" : "modopt")}({modifier.QualifiedName})"),
            Resolved = unmodifiedType.Resolved && modifier.Resolved
        };

    public UnityApiUsageTypeName GetPinnedType(UnityApiUsageTypeName elementType)
        => elementType with { Name = Bound(elementType.Name + " pinned") };

    public UnityApiUsageTypeName GetPointerType(UnityApiUsageTypeName elementType)
        => elementType with { Name = Bound(elementType.Name + "*") };

    public UnityApiUsageTypeName GetPrimitiveType(PrimitiveTypeCode typeCode)
        => new(EncodeToken("System.Runtime"), "System." + typeCode);

    public UnityApiUsageTypeName GetSZArrayType(UnityApiUsageTypeName elementType)
        => elementType with { Name = Bound(elementType.Name + "[]") };

    public UnityApiUsageTypeName GetTypeFromDefinition(MetadataReader reader,
        TypeDefinitionHandle handle, byte rawTypeKind) => FromDefinition(handle, 0);

    public UnityApiUsageTypeName GetTypeFromReference(MetadataReader reader,
        TypeReferenceHandle handle, byte rawTypeKind) => FromReference(handle, 0);

    public UnityApiUsageTypeName GetTypeFromSpecification(MetadataReader reader,
        object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        if (_specificationDepth >= MaxDepth)
            throw new InvalidDataException("Type specification depth exceeds the metadata limit.");
        _specificationDepth++;
        try
        {
            BlobHandle signature = reader.GetTypeSpecification(handle).Signature;
            ChargeSignature(signature);
            UnityApiUsageSignaturePreflight.CheckTypeSpecification(reader, signature);
            return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        }
        finally
        {
            _specificationDepth--;
        }
    }

    private static string Bound(string value)
        => value.Length <= MaxTextLength ? value : throw new InvalidDataException("A metadata name exceeds the text limit.");

    internal static string EncodeToken(string value)
    {
        if (value.Length > MaxTextLength)
            throw new InvalidDataException("A metadata name exceeds the text limit.");
        var encoded = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or
                (>= '0' and <= '9') or '_' or '$' or '`')
                encoded.Append(character);
            else
                encoded.Append('%').Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            if (encoded.Length > MaxTextLength)
                throw new InvalidDataException("An encoded metadata name exceeds the text limit.");
        }
        return encoded.ToString();
    }
}
