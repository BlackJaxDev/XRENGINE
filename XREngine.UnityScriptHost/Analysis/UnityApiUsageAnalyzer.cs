using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using XREngine.UnityScriptHost.Execution;

namespace XREngine.UnityScriptHost.Analysis;

/// <summary>Inspects external method and field references without loading or executing an assembly.</summary>
public sealed class UnityApiUsageAnalyzer
{
    private const int MaxAssemblyBytes = 64 * 1024 * 1024;
    private const int MaxMemberReferences = 100_000;
    private const int MaxDistinctMembers = 16_384;
    private const int MaxReportCharacters = 8 * 1024 * 1024;
    private const int MaxSignatureLength = 4096;

    /// <summary>
    /// Reads a managed PE and classifies each external MemberRef by an assembly-qualified key.
    /// A key has the form `Assembly::Type::Method#Header/Arity/Required([Assembly]Param)->[Assembly]Return`
    /// or `Assembly::Type::Field:[Assembly]FieldType`. Metadata generic parameters
    /// use `!N` or `!!N`. The catalog must use these exact keys.
    /// </summary>
    public UnityApiUsageReport Analyze(byte[] assemblyBytes, UnityCompatibilityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(assemblyBytes);
        ArgumentNullException.ThrowIfNull(catalog);
        if (assemblyBytes.Length == 0 || assemblyBytes.Length > MaxAssemblyBytes)
            throw new InvalidDataException("The inspected assembly exceeds the byte limit or is empty.");

        // Freeze the caller's buffer so its metadata and reported hash name the same bytes.
        byte[] capturedBytes = (byte[])assemblyBytes.Clone();

        try
        {
            using var stream = new MemoryStream(capturedBytes, writable: false);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                throw new InvalidDataException("The inspected input has no managed metadata.");
            MetadataReader metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
                throw new InvalidDataException("The inspected input is not a managed assembly.");
            if (metadata.MemberReferences.Count > MaxMemberReferences)
                throw new InvalidDataException("Member references exceed the metadata limit.");
            string assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            if (string.IsNullOrWhiteSpace(assemblyName) || assemblyName.Length > 1024)
                throw new InvalidDataException("The metadata assembly name is invalid or too long.");
            string encodedAssemblyName = UnityApiUsageTypeProvider.EncodeToken(assemblyName);

            var provider = new UnityApiUsageTypeProvider(metadata, encodedAssemblyName);
            var distinct = new SortedDictionary<string, UnityApiUsageMember>(StringComparer.Ordinal);
            int reportCharacters = 0;
            foreach (MemberReferenceHandle handle in metadata.MemberReferences)
            {
                MemberReference member = metadata.GetMemberReference(handle);
                provider.BeginMember();
                UnityApiUsageTypeName owner = provider.FromParent(member.Parent);
                if (owner.IsLocal)
                    continue;

                provider.ChargeSignature(member.Signature);
                UnityApiUsageSignaturePreflight.CheckMember(metadata, member.GetKind(), member.Signature);

                string name = metadata.GetString(member.Name);
                if (string.IsNullOrWhiteSpace(name) || name.Length > 1024)
                    throw new InvalidDataException("A member name is invalid or too long.");
                string kind;
                string signature;
                bool signatureResolved;
                if (member.GetKind() == MemberReferenceKind.Method)
                {
                    MethodSignature<UnityApiUsageTypeName> method = member.DecodeMethodSignature(provider, null);
                    if (method.ParameterTypes.Length > 1024)
                        throw new InvalidDataException("A method signature has too many parameters.");
                    string parameterTypes = string.Join(",", method.ParameterTypes.Select(
                        static type => type.QualifiedName));
                    signature = string.Create(CultureInfo.InvariantCulture,
                        $"#{method.Header.RawValue:x2}/{method.GenericParameterCount}/{method.RequiredParameterCount}({parameterTypes})->{method.ReturnType.QualifiedName}");
                    signatureResolved = method.ReturnType.Resolved && method.ParameterTypes.All(
                        static type => type.Resolved);
                    kind = "Method";
                }
                else if (member.GetKind() == MemberReferenceKind.Field)
                {
                    UnityApiUsageTypeName fieldType = member.DecodeFieldSignature(provider, null);
                    signature = ":" + fieldType.QualifiedName;
                    signatureResolved = fieldType.Resolved;
                    kind = "Field";
                }
                else
                    throw new InvalidDataException("A member reference has an unsupported signature kind.");

                if (signature.Length > MaxSignatureLength)
                    throw new InvalidDataException("A member signature exceeds the report text limit.");
                string key = $"{owner.Assembly}::{owner.Name}::{UnityApiUsageTypeProvider.EncodeToken(name)}{signature}";
                if (key.Length > MaxSignatureLength)
                    throw new InvalidDataException("A member key exceeds the report text limit.");
                if (distinct.ContainsKey(key))
                    continue;
                if (distinct.Count == MaxDistinctMembers)
                    throw new InvalidDataException("Distinct member references exceed the report limit.");
                reportCharacters = checked(reportCharacters + key.Length + owner.Name.Length +
                    owner.Assembly.Length + name.Length + signature.Length);
                if (reportCharacters > MaxReportCharacters)
                    throw new InvalidDataException("Member references exceed the report byte budget.");
                bool catalogEntryPresent = catalog.TryGetStatus(key, out UnityCapabilityStatus declaredStatus);
                distinct.Add(key, new UnityApiUsageMember
                {
                    AssemblyName = owner.Assembly,
                    TypeName = owner.Name,
                    MemberName = name,
                    Kind = kind,
                    Signature = signature,
                    CatalogKey = key,
                    DeclaringTypeResolved = owner.Resolved,
                    SignatureResolved = signatureResolved,
                    CatalogEntryPresent = catalogEntryPresent,
                    Status = owner.Resolved && signatureResolved && catalogEntryPresent
                        ? declaredStatus : UnityCapabilityStatus.Unsupported
                });
            }

            return new UnityApiUsageReport
            {
                AssemblyName = assemblyName,
                AssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(capturedBytes)),
                MemberReferenceCount = metadata.MemberReferences.Count,
                Members = [.. distinct.Values]
            };
        }
        catch (Exception exception) when (exception is BadImageFormatException or ArgumentOutOfRangeException or
            OverflowException or InvalidOperationException)
        {
            throw new InvalidDataException("The inspected assembly has invalid or unsupported metadata.", exception);
        }
    }
}
