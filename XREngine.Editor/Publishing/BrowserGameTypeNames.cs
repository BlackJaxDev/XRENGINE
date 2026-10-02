using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace XREngine.Editor.Publishing;

/// <summary>An assembly-qualified metadata type name, without loading its declaring assembly.</summary>
internal readonly record struct BrowserGameTypeName(string Assembly, string FullName, string? ConstructedName = null)
{
    internal string SignatureName => ConstructedName ?? FullName;
    internal string SignatureKey => string.IsNullOrEmpty(Assembly) ? SignatureName : Assembly + ":" + SignatureName;
}

/// <summary>Finds type owners through nested references and constructed generic signatures.</summary>
internal sealed class BrowserGameTypeNames(MetadataReader metadata, string currentAssembly)
    : ISignatureTypeProvider<BrowserGameTypeName, object?>
{
    private int _signatureDepth;

    internal BrowserGameTypeName FromReference(TypeReferenceHandle handle) => FromReference(handle, 0);

    private BrowserGameTypeName FromReference(TypeReferenceHandle handle, int depth)
    {
        if (depth == 64)
            throw new InvalidDataException("BrowserGameAudit.InvalidMetadata: nested type reference depth exceeds the audit limit.");
        TypeReference type = metadata.GetTypeReference(handle);
        string name = metadata.GetString(type.Name);
        string ns = metadata.GetString(type.Namespace);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            BrowserGameTypeName parent = FromReference((TypeReferenceHandle)type.ResolutionScope, depth + 1);
            return new(parent.Assembly, parent.FullName + "+" + name);
        }
        string assembly = type.ResolutionScope.Kind == HandleKind.AssemblyReference
            ? metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name)
            : currentAssembly;
        return new(assembly, string.IsNullOrEmpty(ns) ? name : ns + "." + name);
    }

    internal BrowserGameTypeName FromDefinition(TypeDefinitionHandle handle) => FromDefinition(handle, 0);

    private BrowserGameTypeName FromDefinition(TypeDefinitionHandle handle, int depth)
    {
        if (depth == 64)
            throw new InvalidDataException("BrowserGameAudit.InvalidMetadata: nested type definition depth exceeds the audit limit.");
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(type.Name);
        TypeDefinitionHandle parent = type.GetDeclaringType();
        if (!parent.IsNil)
            return new(currentAssembly, FromDefinition(parent, depth + 1).FullName + "+" + name);
        string ns = metadata.GetString(type.Namespace);
        return new(currentAssembly, string.IsNullOrEmpty(ns) ? name : ns + "." + name);
    }

    internal BrowserGameTypeName? FromParent(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => FromReference((TypeReferenceHandle)parent),
        HandleKind.TypeDefinition => FromDefinition((TypeDefinitionHandle)parent),
        HandleKind.TypeSpecification => metadata.GetTypeSpecification((TypeSpecificationHandle)parent).DecodeSignature(this, null),
        _ => null
    };

    public BrowserGameTypeName GetArrayType(BrowserGameTypeName elementType, ArrayShape shape)
    {
        string suffix = shape.Rank == 1 ? "[*]" : "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";
        return new(elementType.Assembly, elementType.FullName + suffix, elementType.SignatureName + suffix);
    }
    public BrowserGameTypeName GetByReferenceType(BrowserGameTypeName elementType)
        => new(elementType.Assembly, elementType.FullName + "&", elementType.SignatureName + "&");
    public BrowserGameTypeName GetFunctionPointerType(MethodSignature<BrowserGameTypeName> signature)
        => new("", "fnptr", "fnptr(" + string.Join(",", signature.ParameterTypes.Select(type => type.SignatureKey))
            + ")->" + signature.ReturnType.SignatureKey);
    public BrowserGameTypeName GetGenericInstantiation(BrowserGameTypeName genericType, ImmutableArray<BrowserGameTypeName> typeArguments)
        => new(genericType.Assembly, genericType.FullName,
            genericType.FullName + "<" + string.Join(",", typeArguments.Select(type => type.SignatureKey)) + ">");
    public BrowserGameTypeName GetGenericMethodParameter(object? genericContext, int index) => new("", "!!" + index);
    public BrowserGameTypeName GetGenericTypeParameter(object? genericContext, int index) => new("", "!" + index);
    public BrowserGameTypeName GetModifiedType(BrowserGameTypeName modifier, BrowserGameTypeName unmodifiedType, bool isRequired)
        => new(unmodifiedType.Assembly, unmodifiedType.FullName,
            unmodifiedType.SignatureName + (isRequired ? " modreq(" : " modopt(") + modifier.SignatureKey + ")");
    public BrowserGameTypeName GetPinnedType(BrowserGameTypeName elementType)
        => new(elementType.Assembly, elementType.FullName, elementType.SignatureName + " pinned");
    public BrowserGameTypeName GetPointerType(BrowserGameTypeName elementType)
        => new(elementType.Assembly, elementType.FullName + "*", elementType.SignatureName + "*");
    public BrowserGameTypeName GetPrimitiveType(PrimitiveTypeCode typeCode) => new("System.Runtime", "primitive:" + typeCode);
    public BrowserGameTypeName GetSZArrayType(BrowserGameTypeName elementType)
        => new(elementType.Assembly, elementType.FullName + "[]", elementType.SignatureName + "[]");
    public BrowserGameTypeName GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => FromDefinition(handle);
    public BrowserGameTypeName GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => FromReference(handle);
    public BrowserGameTypeName GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        if (_signatureDepth == 64)
            throw new InvalidDataException("BrowserGameAudit.InvalidMetadata: type specification depth exceeds the audit limit.");
        _signatureDepth++;
        try { return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext); }
        finally { _signatureDepth--; }
    }
}
